# 异环小地图朝上

双击 `启动小地图.cmd`。需要本机已有的 .NET 9 Windows Desktop Runtime。

- 默认在原地图右侧显示旋转后的预览；勾选“覆盖原小地图”或按 Ctrl+Alt+F9 切换。
- Ctrl+Alt+F8：暂停 / 恢复。Ctrl+Alt+F10：退出。
- 控制面板最小化后进入托盘，双击托盘图标恢复；右键可以退出。
- 只在 HTGame.exe 位于前台且检测到小地图及中心金色箭头时显示。切出、最小化、地图消失或识别失败时隐藏；重新检测到后连续确认三帧再显示。

当前是按提供的 3840×2160 截图校准的原型。随游戏客户区高度缩放，在左上角附近搜索圆形结构，针对中心金色箭头匹配 120 个方向。支持明暗边界和暗背景下的圆环亮边，排除高饱和黄色导航线。新 HUD 布局、HUD 缩放、不同样式的箭头、遮挡以及 HDR 捕获表现仍需实际验证。

捕获使用 Windows GDI 的 CopyFromScreen，仅截取左上角一小块区域，目标刷新间隔 33 ms；不是 GPU 捕获版，也没有测量端到端显示延迟。覆盖窗口使用圆形裁剪、置顶、无激活和鼠标穿透，并通过 WDA_EXCLUDEFROMCAPTURE 排除自身捕获；排除失败时停用。建议无边框窗口模式，独占全屏能否显示覆盖层需现场测试。

`app/settings.json` 首次修改覆盖模式时生成，可在退出后修改：CenterX / CenterY / Radius 是游戏客户区高度归一化到 1080 后的位置；默认 156 / 140 / 108。IntervalMs 为刷新间隔，ArrowThreshold / RingThreshold 为检测门槛。恢复默认可在退出后移走此文件。`app/status.json` 是最近状态，`app/minimap.log` 记录状态切换。

只捕获屏幕像素并查询窗口，不读取游戏内存。所有生成文件保存在本目录；没有安装自启动或修改游戏文件。

## 构建和验证

```powershell
dotnet publish -c Release -o app -p:NuGetAudit=false
.\app\MinimapUp.exe --test (Resolve-Path .\reference.png).Path
.\app\MinimapUp.exe --analyze (Resolve-Path .\failure-angle.png).Path
```

`--test` 在程序目录生成 test-results.json，覆盖原截图、23 个旋转方向、纯黑无地图和只有箭头无地图样本。旋转测试是离线变换验证，不能代替实际驾驶验证。`--analyze` 在输入截图旁输出 detection.json；`--capture` 针对当前 HTGame 客户区进行一次桌面捕获，主要用于调试，游戏应保持前台。

此次验证：原始截图和用户提供的暗背景漏检截图均通过 24 个方向检测；后者离线旋转相对角度误差最大 6°；实际拍照界面截图被拒绝。实测一帧处理时间约 6–14 ms，仅代表本次机器与样本，不是总延迟。
