# FluentShell 产品启动图标提示词

仅在设计或修改应用启动图标时读取。界面内的单色命令图标使用 [fluent-system-icons](../.agents/skills/fluent-system-icons/SKILL.md)。本文记录 FluentShell 的图标方向，具体任务中的用户要求优先。

## 设计约束

- 以终端 / Shell 为主要隐喻，保持单一、可识别的轮廓；产品名不需要逐字转成装饰元素。
- 透明背景上的独立轮廓，少量正视的平坦重叠形状。圆角用于局部轮廓，避免整张画布变成统一圆角底板。
- 使用蓝—青同类色、克制渐变和用于分开层次的轻微阴影。图标在明暗背景和小尺寸下都应清楚。
- 创作原创图形，保留 Fluent 的视觉一致性；不复制微软产品标志。

这些是本项目的视觉选择。下列官方资料提供一般图标构造原则，不把某个模型的生成习惯当成通用规则。

## 可直接使用的提示词

```text
Create one original product-launch icon for FluentShell, a native Windows SSH/SFTP client.
Use a terminal or command-shell metaphor with a distinctive silhouette. Build it from a few broad, front-facing, flat overlapping shapes on a transparent background, with soft local corners and subtle shadows only between layers.
Use a restrained blue-to-cyan palette that reads clearly on both light and dark Windows backgrounds. A small command chevron or cursor detail is optional. Keep the design recognizable at 24 px.
Deliver one centered RGBA icon with transparent padding. Keep it free of lettering, extra feature badges, enclosing app tiles, and copied Microsoft logos.
```

任务需要中文提示词时可以直接翻译，不要求固定语言。若有参考图，只在文件实际可用且用户希望使用时附加；当前流程不依赖仓库外的旧参考图。

## 生成与验收

先检查一次生成的结果；只有隐喻或构图仍不明确时才增加候选。针对可见问题修改提示词，不预设必须生成四轮或重复报告全部约束。

交付前检查透明通道、独立轮廓、明暗背景可读性和实际使用尺寸。Windows 应用资源通常需要 16、24、32、48、256 px；针对小尺寸简化细节，并按项目清单核对所需资源。不要仅凭提示词要求透明就声称文件具有透明通道。

## 官方来源

- [Fluent 2 iconography](https://fluent2.microsoft.design/iconography)：system、product launch 和 file 图标是不同集合。
- [Windows app icon design](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-design)：隐喻、轮廓、几何、层次与颜色原则。
- [Windows app icon construction](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-construction)：透明背景、尺寸和应用资源。
