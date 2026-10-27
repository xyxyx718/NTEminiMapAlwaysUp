# 本地测试样本

`fixtures/` 中保留游戏截图，未纳入 Git：

- `reference.png`：最早的小地图 / 图标参考，检测与旋转回归。
- `failure-angle.png`：暗背景漏检回归。
- `driving-keyboard.png`、`driving-playstation.png`、`driving-xbox.png`、`driving-rain-car.png`、`driving-motorcycle.png`：驾驶文字识别样本。
- `live-game.png`：没有小地图的拍照界面，作为驾驶识别的负例。
- `live-map.png`：开发期间的实际小地图截图。
- `driving-rearview-only.png`：局部文字裁剪实验，不作为完整驾驶 UI 的通过标准。

`BehaviorTests.cs` 通过程序的 `--test-behavior` 入口运行。默认显示 / 隐藏时间均为 100 ms；时序测试还显式使用 200 ms 等参数，以保留非默认设置下的覆盖。

旧诊断报告位于 `artifacts/reports/`；截图和报告移动时保留原文件内容。
