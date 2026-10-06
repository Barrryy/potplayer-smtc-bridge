#pragma once

// 捕获 SMTC 对象并列出它实现的所有接口 IID。
//
// 设计原则：全程不依赖任何硬编码 IID。
//   1. RoGetActivationFactory 被挂钩后，拿到 PotPlayer 请求的类名与激活工厂指针；
//   2. 在该工厂的 vtable 上挂钩 GetForWindow
//      （IInspectable 占 6 槽，它是 ISystemMediaTransportControlsInterop 的唯一方法 → 下标 6）；
//   3. PotPlayer 调用 GetForWindow 时，原样记录 HWND / riid / 返回指针；
//   4. 对返回对象调用 IInspectable::GetIids（vtable 下标 3），把它实现的所有 IID 打出来。
//
// 第 4 步是关键：IID 由对象自己汇报，不需要我们猜。

void SmtcCapture_OnActivation(void* hstringClassId, void* factory);

// 主动出击：自己去激活 SMTC 的 interop 工厂，并在它的 GetForWindow 上挂钩。
//
// 为什么需要这一步：实测 MediaDB64.dll 加载后不到 10ms 就创建了 SMTC 对象，
// 靠「等它自己激活」或轮询补挂都会慢一步。而 WinRT 的激活工厂在进程内是缓存的，
// 我们提前拿到同一个工厂并挂钩，MediaDB 之后的调用就必然经过我们。
// roGetActivationFactory：传入真实的（未被改写的）函数地址
void SmtcCapture_Start(void* roGetActivationFactory);
