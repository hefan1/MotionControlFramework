using System.Threading;
using MotionControl.Framework.Contracts.Flow;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Execution;

namespace MotionControl.TaskEngine.Machine
{
    /// <summary>请求和完成两变量的四阶段交接，完成方不擅自清请求。</summary>
    public sealed partial class TaskMachineController
    {
        /// <summary>旧完成为 true 时先等待请求复归，不把上一周期信号当作新请求。</summary>
        private async Task WaitRequestAsync(StationExecutionControl station, CancellationToken token)
        {
            bool accepted = false;
            while (!accepted)
            {
                await StepAsync(() =>
                {
                    bool? request, completed;
                    _device.ReadHandshake(station.Configuration.RequestVariable, station.Configuration.CompletedVariable, out request, out completed);
                    station.State = StationTaskState.WaitingRequest;
                    station.Detail = "等待请求：" + station.Configuration.RequestVariable + "=" + Show(request)
                        + "；" + station.Configuration.CompletedVariable + "=" + Show(completed);
                    string reasonCode = !request.HasValue || !completed.HasValue ? "handshake.unknown"
                        : completed.Value ? (request.Value ? "handshake.wait_request_reset" : "handshake.wait_completion_reset")
                        : request.Value ? "handshake.request_ready" : "handshake.wait_request";
                    station.Diagnostics = DescribeHandshake(station, request, completed,
                        completed == true ? false : true, true, reasonCode);
                    if (request == false && completed == true)
                        _device.TryClearCompletion(station.Configuration.RequestVariable, station.Configuration.CompletedVariable);
                    else accepted = request == true && completed == false;
                }, token).ConfigureAwait(false);
                if (!accepted) await Task.Delay(10, token).ConfigureAwait(false);
            }
        }

        /// <summary>动作成功后发布完成；提前撤销请求会锁存故障，恢复只重做交接，不重做已完成动作。</summary>
        private async Task CompleteRequestAsync(StationExecutionControl station, CancellationToken token)
        {
            bool published = false;
            while (!published)
            {
                await StepAsync(() => published = _device.TryPublishCompletion(
                    station.Configuration.RequestVariable, station.Configuration.CompletedVariable), token).ConfigureAwait(false);
                if (!published)
                {
                    var error = new TaskExecutionException(station.Configuration.TaskId, "__handshake", null,
                        "交接请求在完成前被撤销或无效：" + station.Configuration.RequestVariable + "。请恢复请求后复位启动；不会重做已完成动作。");
                    station.Diagnostics = DescribeHandshake(station, null, null, true, false,
                        "handshake.publish_not_accepted").WithFailure(error);
                    Task permit = await ReportNodeFaultAsync(station, error).ConfigureAwait(false);
                    await StationExecutionControl.WaitSignalAsync(permit, token).ConfigureAwait(false);
                }
            }
            bool cleared = false;
            while (!cleared)
            {
                await StepAsync(() =>
                {
                    station.State = StationTaskState.WaitingRequest;
                    station.Detail = "已完成，等待请求方清除 " + station.Configuration.RequestVariable + " 后复归完成信号。";
                    station.Diagnostics = DescribeHandshake(station, null, null, false, false,
                        "handshake.wait_request_reset_after_completion");
                    cleared = _device.TryClearCompletion(station.Configuration.RequestVariable, station.Configuration.CompletedVariable);
                }, token).ConfigureAwait(false);
                if (!cleared) await Task.Delay(10, token).ConfigureAwait(false);
            }
        }

        /// <summary>分别保存请求与完成两个对象；未实际读取的阶段不补造布尔值。</summary>
        private static TaskStepDiagnostics DescribeHandshake(StationExecutionControl station, bool? request,
            bool? completed, bool expectedRequest, bool sampled, string reasonCode)
        {
            StationValueQuality requestQuality = !sampled ? StationValueQuality.NotProvided
                : request.HasValue ? StationValueQuality.Good : StationValueQuality.Unknown;
            StationValueQuality completedQuality = !sampled ? StationValueQuality.NotProvided
                : completed.HasValue ? StationValueQuality.Good : StationValueQuality.Unknown;
            var items = new[] {
                new StationWaitItem(StationWaitKind.Handshake, reasonCode, StationObjectKind.Variable,
                    station.Configuration.RequestVariable, station.Configuration.RequestVariable,
                    expectedRequest.ToString(), request?.ToString(), requestQuality,
                    sampled && request.HasValue ? (bool?)(request.Value == expectedRequest) : null,
                    description: station.Detail),
                new StationWaitItem(StationWaitKind.Handshake, reasonCode, StationObjectKind.Variable,
                    station.Configuration.CompletedVariable, station.Configuration.CompletedVariable,
                    false.ToString(), completed?.ToString(), completedQuality,
                    sampled && completed.HasValue ? (bool?)!completed.Value : null, description: station.Detail) };
            return new TaskStepDiagnostics(station.Detail, waitKind: StationWaitKind.Handshake,
                reasonCode: reasonCode, waitItems: items);
        }

        /// <summary>未知信号单独显示，不当作 OFF。</summary>
        private static string Show(bool? value)
        {
            return value.HasValue ? value.Value.ToString() : "未知";
        }
    }
}
