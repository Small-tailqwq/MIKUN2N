# MikuN2N 标志

[English](README.md) | [简体中文](README.zh.md)

C1 将直立、硬朗的 M 与双马尾形态结合。顶部的红色嵌片，以及右下方的青、黄、橙色块，呼应赛车未来 2016。灰阶版与浅灰版用实色灰阶逐块还原彩色版，保留肩部折面、中央折面和橙色色块的下沿；相邻色块的明暗构成边界，无需透明切缝。

| 文件 | 用途 |
| --- | --- |
| `mikun2n-mark-color.svg` | 独立彩色标志 |
| `mikun2n-mark-gray.svg` | 用于浅色背景的灰阶标志 |
| `mikun2n-mark-light.svg` | 用于深色背景的浅灰标志 |
| `mikun2n-mark-black.svg` | 带细透明切缝的深色纯单色标志 |
| `mikun2n-mark-white.svg` | 带细透明切缝的白色纯单色标志 |
| `mikun2n-lockup-color.svg` | 彩色标志与深色 MikuN2N 字标组合 |
| `mikun2n-lockup-gray.svg` | 灰阶标志与深色字标组合 |
| `mikun2n-lockup-light.svg` | 浅灰标志与浅色字标组合 |
| `mikun2n-lockup-black.svg` | 深色纯单色标志与字标组合 |
| `mikun2n-lockup-white.svg` | 白色纯单色标志与字标组合 |
| `preview.png` | 彩色、灰阶与浅灰图稿总览 |

全部独立标志均使用 800 × 800 的正方形画布，原有轮廓居中放置、不拉伸，预览底板也为正方形。带字标的组合使用 800 × 690 画布。

全部 SVG 均为透明背景，包含可编辑的矢量路径，文字也已转为轮廓；无需安装字体，不含嵌入位图、蒙版或外部资源。使用时保持等比缩放。纯黑、纯白版用于只能使用单一墨色的场景，在 800 单位画布上使用宽 2.4 单位的细切缝；修改路径填充色即可换色，切缝仍然透明。

程序使用彩色标志。`App.xaml` 将其矢量路径保存为共享的 `MikuN2NLogo` 图形，供主界面和关于页显示，`Services/AppIconService.cs` 也将它用于窗口图标。`MikuN2N.ico` 从 `mikun2n-mark-color.svg` 渲染得到，含 16、20、24、32、40、48、64、128、256 像素帧，EXE 和托盘共用此文件。修改标志时，需将这些派生资产与彩色 SVG 同步。
