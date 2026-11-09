param(
    [Parameter(Mandatory = $true)][string]$SourceRoot,
    [string]$Installer = (Join-Path (Split-Path $PSScriptRoot) 'Install-RecentSimulationProject.ps1'),
    [string]$ResultsRoot = $env:TEMP
)

$ErrorActionPreference='Stop'
$sourceFull=(Resolve-Path -LiteralPath $SourceRoot).Path
$installerFull=(Resolve-Path -LiteralPath $Installer).Path
$base=Join-Path ([IO.Path]::GetFullPath($ResultsRoot)) ('InstallRecentChecks-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($base)|Out-Null
$originalLocal=$env:LOCALAPPDATA
$results=@()
try {
    foreach($scenario in @('task-pending','project-pending','null-axes','missing-io','draft')){
        $source=Join-Path $base ('source-'+$scenario)
        Copy-Item -LiteralPath $sourceFull -Destination $source -Recurse
        $machine=Join-Path $source 'Machine01'
        $env:LOCALAPPDATA=Join-Path $base ('local-'+$scenario)
        [IO.Directory]::CreateDirectory($env:LOCALAPPDATA)|Out-Null
        switch($scenario){
            'task-pending' {$folder=Join-Path $machine 'Drafts';[IO.Directory]::CreateDirectory($folder)|Out-Null;[IO.File]::WriteAllText((Join-Path $folder 'TaskApplyPending.json'),'{}')}
            'project-pending' {[IO.File]::WriteAllText((Join-Path $machine 'Runtime\EquipmentProjectApplyPending.json'),'{}')}
            'null-axes' {$path=Join-Path $machine 'HardwareConfig\AxisParameters.json';$value=Get-Content -LiteralPath $path -Raw -Encoding UTF8|ConvertFrom-Json;$value.axes=$null;[IO.File]::WriteAllText($path,($value|ConvertTo-Json -Depth 30))}
            'missing-io' {$path=Join-Path $machine 'HardwareConfig\IoMapping.json';$value=Get-Content -LiteralPath $path -Raw -Encoding UTF8|ConvertFrom-Json;$value.PSObject.Properties.Remove('digitalInputs');[IO.File]::WriteAllText($path,($value|ConvertTo-Json -Depth 30))}
            'draft' {$folder=Join-Path $machine 'Drafts';[IO.Directory]::CreateDirectory($folder)|Out-Null;[IO.File]::WriteAllText((Join-Path $folder 'TaskWorkspace.json'),'{"schemaVersion":1,"note":"keep draft"}')}
        }
        $log=Join-Path $base ($scenario+'.log')
        $ErrorActionPreference='Continue'
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installerFull -SourceRoot $source *> $log
        $exitCode=$LASTEXITCODE
        $ErrorActionPreference='Stop'
        $preference=Join-Path $env:LOCALAPPDATA 'MotionControlFramework\DefaultSimulationProject.txt'
        if($scenario -eq 'draft'){
            $draft=Join-Path $env:LOCALAPPDATA 'MotionControlFramework\SimulationProjects\RecentSandboxProject\Machine01\Drafts\TaskWorkspace.json'
            $firstId=Join-Path $env:LOCALAPPDATA 'MotionControlFramework\SimulationProjects\RecentSandboxProject\ProjectSession.id'
            $passed=$exitCode -eq 0 -and (Test-Path -LiteralPath $preference) -and (Test-Path -LiteralPath $draft) -and [IO.File]::ReadAllText($draft).Contains('keep draft') -and (Test-Path -LiteralPath $firstId) -and [IO.File]::ReadAllText($firstId) -match '^[0-9a-f]{32}$'
        } else {
            $expected=if($scenario -like '*pending'){'未完成的配置应用'}else{'轴或 IO 映射无效'}
            $passed=$exitCode -ne 0 -and -not (Test-Path -LiteralPath $preference) -and (Get-Content -LiteralPath $log -Raw).Contains($expected)
        }
        $results+=@{scenario=$scenario;exitCode=$exitCode;passed=$passed}
        if($scenario -eq 'draft' -and $passed){
            $ErrorActionPreference='Continue'
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installerFull -SourceRoot $source -ProjectName 'SecondProject' *> (Join-Path $base 'second-project.log')
            $secondExit=$LASTEXITCODE
            $ErrorActionPreference='Stop'
            $secondId=Join-Path $env:LOCALAPPDATA 'MotionControlFramework\SimulationProjects\SecondProject\ProjectSession.id'
            $unique=$secondExit -eq 0 -and (Test-Path -LiteralPath $secondId) -and [IO.File]::ReadAllText($firstId) -ne [IO.File]::ReadAllText($secondId) -and (Test-Path -LiteralPath $draft)
            $results+=@{scenario='distinct-project-session';exitCode=$secondExit;passed=$unique}
        }
    }

    # 安装进行中分别创建同名目标和修改源文件；两种情况均不得切换默认入口。
    foreach($race in @('target','source')){
        $source=Join-Path $base ('source-race-'+$race)
        Copy-Item -LiteralPath $sourceFull -Destination $source -Recurse
        $many=Join-Path $source 'Machine01\HardwareConfig\RaceFiles'
        [IO.Directory]::CreateDirectory($many)|Out-Null
        for($i=0;$i -lt 350;$i++){[IO.File]::WriteAllText((Join-Path $many ('{0:D4}.json' -f $i)), '{"value":1}')}
        $env:LOCALAPPDATA=Join-Path $base ('local-race-'+$race)
        $root=Join-Path $env:LOCALAPPDATA 'MotionControlFramework\SimulationProjects'
        $destination=Join-Path $root 'RecentSandboxProject'
        $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$installerFull+'"'),'-SourceRoot',('"'+$source+'"'))
        $process=Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $base ('race-'+$race+'-out.log')) -RedirectStandardError (Join-Path $base ('race-'+$race+'-err.log'))
        $processHandle=$process.Handle
        $observed=$false
        for($i=0;$i -lt 3000;$i++){
            $process.Refresh();if($process.HasExited){break}
            if((Test-Path -LiteralPath $root) -and @(Get-ChildItem -LiteralPath $root -Directory -Force|Where-Object {$_.Name -like '.install-*'}).Count -gt 0){
                if($race -eq 'target'){
                    [IO.Directory]::CreateDirectory($destination)|Out-Null
                    [IO.File]::WriteAllText((Join-Path $destination 'existing-user-data.txt'),'do not overwrite')
                } else { [IO.File]::AppendAllText((Join-Path $source 'Machine01\Parameters\TaskMachine.json'),' ') }
                $observed=$true;break
            }
            Start-Sleep -Milliseconds 10
        }
        if(-not $process.WaitForExit(60000)){throw '安装竞态检查没有按时结束。'}
        $process.Refresh();$exitCode=$process.ExitCode
        $preference=Join-Path $env:LOCALAPPDATA 'MotionControlFramework\DefaultSimulationProject.txt'
        $passed=$observed -and $null -ne $exitCode -and $exitCode -ne 0 -and -not (Test-Path -LiteralPath $preference)
        if($race -eq 'target'){
            $sentinel=Join-Path $destination 'existing-user-data.txt'
            $passed=$passed -and (Test-Path -LiteralPath $sentinel) -and [IO.File]::ReadAllText($sentinel) -eq 'do not overwrite'
        } else {
            $errorText=Get-Content -LiteralPath (Join-Path $base ('race-'+$race+'-err.log')) -Raw
            $passed=$passed -and -not (Test-Path -LiteralPath $destination) -and $errorText.Contains('源配置在复制期间发生变化')
        }
        $results+=@{scenario=($race+'-changed-during-install');exitCode=$exitCode;passed=$passed}
    }
} finally {$env:LOCALAPPDATA=$originalLocal}
$failures=@($results|Where-Object {-not $_.passed})
@{results=$results;failureCount=$failures.Count;detailsDirectory=$base}|ConvertTo-Json -Depth 5|Set-Content -Encoding UTF8 (Join-Path $base 'summary.json')
if($failures.Count){throw ('安装检查失败：'+(($failures|ForEach-Object {$_.scenario}) -join ', '))}
Write-Output "安装检查全部通过，结果：$base"
