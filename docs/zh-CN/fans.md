# 风扇读数

展开的系统监控页默认在功耗下方显示 CPU、GPU 两路风扇：各自的 `F` 圆环、转速比例和 RPM。窄尺寸保留比例，RPM 可悬停查看。“设置 › 系统监控 › 展开时 › 显示风扇”单独控制此行，不依赖胶囊中的风扇开关；未支持的电脑不显示此行。

在“设置 › 系统监控”的收起读数表中开启“风扇”。`F` 圆环显示两路风扇中较高的转速与最大转速的比例；数值可选择“转速比”或“RPM”，关闭“显示数值”则只保留圆环。悬停风扇项可查看 CPU、GPU 各自的转速和比例。“风扇最大转速”默认为 5750 RPM，可以在同页调整。

当前支持已验证的 `V15x_V17xPNKPNJPNH` 主板及其已安装的 Control Center。BrimDeck 从原厂 `InsydeDCHU.dll` 的 `GetDCHU_Data_Buffer(12, buffer)` 获取周期计数，按原厂公式换算为 RPM；未支持的电脑隐藏此项。只在读数需要显示时于后台采样，两次读取至少间隔两秒。

此功能仅调用遥测 getter，不调用调速、曲线、风扇模式或功耗模式接口，不安装驱动。CPU、GPU 两路分别保留原始 RPM，圆环比例限制在 0–100%，超过最大转速时仍显示实际 RPM。读取异常不以 0 RPM 代替；现有传感器保护也会停止无响应的调用。

开发验证：

```powershell
dotnet run --project tests/BrimDeck.Tests -c Release
# 以下硬件验证需要上述主板和 Control Center。
dotnet run --project tests/BrimDeck.WindowsTests -c Release -- --only fans
dotnet run --project scripts/readme-media -c Release -p:Platform=x64 -- artifacts/fans/qa zh-CN fans
```
