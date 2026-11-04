// 在 Sandbox 中附加已核实的正式 Host，仅调用只读导航的 UIA InvokePattern。
// 不使用 click、键盘、鼠标移动或设备动作；结束会话不关闭上位机。
const base = 'http://127.0.0.1:4727';
const idKey = 'element-6066-11e4-a52e-4f735466cecf';
async function request(path, method = 'GET', body) {
  const response = await fetch(base + path, { method, headers: { 'content-type': 'application/json' }, body: body ? JSON.stringify(body) : undefined });
  const result = await response.json();
  if (!response.ok) throw new Error(JSON.stringify(result));
  return result.value;
}
async function main() {
  const hwnd = process.argv[2];
  if (!hwnd) throw new Error('需要已核实的 Sandbox Host 窗口句柄。');
  const session = await request('/session', 'POST', { capabilities: { alwaysMatch: {
    platformName: 'Windows', 'appium:automationName': 'Wincore',
    'appium:appTopLevelWindow': hwnd, 'appium:shouldCloseApp': false
  } } });
  const id = session.sessionId;
  async function find(using, value) {
    const result = await request(`/session/${id}/element`, 'POST', { using, value });
    return result[idKey] || result.ELEMENT;
  }
  async function title() {
    const element = await find('accessibility id', 'PageTitle');
    return request(`/session/${id}/element/${element}/text`);
  }
  try {
    const before = await title();
    if (before !== '设备准备') throw new Error(`预期设备准备页面，实际为：${before}`);
    const button = await find('name', '查看生产流程');
    await request(`/session/${id}/execute/sync`, 'POST', { script: 'windows: invoke', args: [{ elementId: button }] });
    const after = await title();
    if (after !== '工位运行') throw new Error(`Invoke 后页面未切换：${after}`);
    console.log(JSON.stringify({ before, after, method: 'windows: invoke', mouseInjection: false, hostClosed: false }));
  } finally { await request(`/session/${id}`, 'DELETE'); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
