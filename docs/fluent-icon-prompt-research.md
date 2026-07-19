# FluentShell Fluent 2 应用图标：官方资料研究与生图提示词

> 研究日期：2026-07-18  
> 范围：仅采用 Microsoft / Fluent 2 官方一手资料，并结合仓库内用户提供的微软产品图标展示图进行视觉观察。

## 结论先行

要让 FluentShell 图标接近 Windows / Fluent 的产品图标语言，提示词不能只写“Fluent Design、渐变、3D”。这类宽泛词很容易把模型带向 macOS Big Sur 的统一圆角方形底板、厚玻璃和强高光。更有效的写法是把 Microsoft 官方规则拆成可检查的几何约束：

1. **先规定一个清楚的主隐喻和独特外轮廓**：FluentShell 只以“终端 / Shell”为主隐喻；产品名不需要被逐字翻译成水流、波纹或书写笔触，也不要同时塞入文件夹、服务器、锁等符号。
2. **明确要求透明画布上的自由轮廓（unplated / free-standing silhouette）**：禁止用覆盖整张图的圆角方形、squircle、瓷砖或容器当底板。
3. **只用少量正视、平坦的重叠形状建立层次**：不是拟物厚块，不做倾斜透视，不做玻璃材质。
4. **圆角只修饰局部轮廓**：官方 48×48 基准中，外角约 2 px、内角约 1 px；这意味着“柔和”，而不是“大圆角方块”。
5. **控制颜色和光影**：以同色系蓝—青为主，渐变平滑、少阶、默认约 120°；左上方轻微环境光，阴影只用来分开重叠层。
6. **先按 48×48 设计，再做小尺寸专版**：16/24/32 px 不应只是把 256 px 大图机械缩小。

## 官方依据

### 1. Fluent 2 设计原则约束的是“体验”，不是一种滤镜

[Fluent 2 Design principles](https://fluent2.microsoft.design/design-principles) 提供四条上位原则，其中对图标最直接的有：

- **Natural on every platform**：体验应适应所在平台并建立在用户熟悉的模式之上。因此 FluentShell 是 Windows 应用时，应优先遵循 Windows 应用图标规范，而不是借用 macOS 的统一 squircle 容器。
- **Built for focus**：减少视觉杂乱与噪音。对应到图标，就是删去不支持核心隐喻的装饰和多余细节。
- **Unmistakably Microsoft**：颜色、插画与图标等 signature experiences 提高熟悉度与品牌识别；同时官方也提醒“一点个性就足够”，不需要堆叠效果来证明风格。

### 2. Fluent 图标首先要可识别、实用、易懂

[Fluent 2 Iconography](https://fluent2.microsoft.design/iconography) 将图标分成 system、product launch 与 file 三类。FluentShell 的启动图标对应 **product launch icon** 的角色，而不是界面内的单色 system icon。官方强调：

- 图标表达概念、对象或动作，应始终可识别、实用并容易理解。
- Product launch icon 用于识别一个应用或能力。
- 小于 48 px 时应主动减少细节以换取可读性，并使用针对具体尺寸制作的版本。
- 修饰符只能建立在简单想法上；如果使画面或含义过于复杂，就不应添加。

### 3. Windows 应用图标有更具体的构图规范

[Design guidelines for Windows app icons](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-design) 给出了可直接转换成提示词的约束：

- **隐喻**：用简单形状将应用概念表现为一个整体；最多两个隐喻，一个更好；主概念必须成为焦点。
- **文字**：避免在图标中使用字母和单词，除非确实不可替代。因而不建议用大写 `F`、`SSH` 或仿 Office 的字母角标作为 FluentShell 主体。
- **网格与圆角**：先对齐 48×48 网格；48 px 下外部曲线圆角约 2 px，内部曲线约 1 px。
- **轮廓**：外轮廓要平衡、独特且在小尺寸清楚；用尽可能少的形状和转角。
- **细节**：额外的具象细节只放在最突出的主层上。
- **颜色**：色彩处理应最少化；渐变通常要克制，横纵方向各只保留一到两个过渡；默认角度约 120°，避免紧促得像反光或强立体高光的过渡。
- **光感**：同色渐变可以暗示来自左上方的轻微环境光，但不应像强烈直射光。
- **层次**：图标由平坦对象叠在下方图层之上；层数尽可能少，阴影只用来区分层与连接部件。
- **视角**：默认正视；除非隐喻无法读懂，否则不使用透视。即使表现体积，图层仍应保持平坦并垂直于视线。

### 4. 透明背景是避免“Big Sur 底板”的关键工程要求

[Construct your Windows app's icon](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-construction) 明确写道，图标在透明背景上通常表现最好。只有品牌确实需要时才应加底板，而且加底板会失去透明图标自动适配主题的部分能力。

这条官方建议直接支持 FluentShell 使用 **透明背景 + 独立轮廓**。同一页还指出：

- Windows 至少应准备 16、24、32、48、256 px 资源。
- Windows 会优先寻找精确尺寸，因此多尺寸专版能减少缩放并提高像素清晰度。
- 为应用列表提供 `altform-unplated` / `altform-lightunplated` 资源，能避免系统另加不理想的背板。

## 对用户参考图的视觉解读

参考图：[designlanguage-iconography-productlaunch.png](../designlanguage-iconography-productlaunch.png)

以下为对该图的直接视觉观察，不把观察冒充成额外的官方文字规范：

- 图标家族靠**相似的色彩体积、柔和局部圆角与少量重叠层**形成一致性，而不是共用一个外部圆角方框。
- 每个产品保留自己的独特外轮廓：云、人物、圆盘、折带、数据库圆柱等都能仅凭剪影区分。
- 有些 Office 图标包含小型字母牌，但它是品牌系统中既有的识别资产，不等于新应用都应模仿“字母 + 方块”。Microsoft 的 Windows 新应用图标规范仍建议避免文字。
- 大多数视觉深度来自层与层的遮挡、色值差和局部阴影；不是厚重挤出的 3D 物体，也不是覆盖整图的玻璃质感。

## FluentShell 的隐喻选择

### 推荐方向：只表达终端 / Shell

- **主隐喻**：一个简化但有独特剪影的终端工作面 / shell 符号。
- **终端细节**：可以在最前层使用一个几何化的命令箭头或短光标切口，但不要画完整 UI、窗口控制点或命令文字。
- **品牌名处理**：将 FluentShell 只视为产品名称，不从 `Fluent` 推导水流、波纹或书写笔触。识别度应来自轮廓、层次和配色，而不是名字的字面插图。
- **远程与 SFTP**：不再额外放连接线、服务器或文件夹。SSH/SFTP 是产品能力，不必各占一个符号；这些能力在应用界面或宣传图中表达。

设计目标不是让用户逐字读出 “SSH/SFTP”，而是在 Windows 启动器中一眼识别“这是一个远程终端工具”，并通过独特轮廓记住 FluentShell。

## 推荐的生图主提示词

建议把用户提供的产品图标展示图作为**风格参考图**一起输入，并使用下面这段英文提示词。英文版对多数生图模型的设计词汇约束更稳定；其中已避免要求复制任何微软产品图标。

```text
Design one original product-launch app icon for “FluentShell”, a native Windows SSH and SFTP client.

Treat “FluentShell” solely as the product name. Do not translate the word “Fluent” into water, waves, or handwriting imagery.

Core metaphor: terminal / command shell only. Create one unified, instantly recognizable and distinctive terminal symbol. Explore an original silhouette made from 2–3 broad overlapping planes rather than a literal screenshot of a terminal window. A single geometric command chevron or short cursor notch may appear as a small pictorial detail on the foremost layer, but it must not look like typed text or a complete user interface.

Use the attached Microsoft Fluent product-launch icon sheet only as a visual-language reference: familiar, friendly, modern, simple, distinctive silhouette, a few flat overlapping shapes, restrained depth, soft local corners, and polished blue-to-cyan color relationships. Create an original shape; do not copy, remix, or include any Microsoft product logo.

Compose on a transparent background as a free-standing unplated silhouette. Start from a 48 × 48 icon grid. Use as few shapes and corners as possible. At the 48 px design basis, keep exterior corner radii near 2 px and interior radii near 1 px. Keep all layers front-facing and flat, with only 2–3 overlapping layers. Use subtle masked shadows only where one layer overlaps another.

Color: a coherent blue / azure / cyan analogous palette with dark, mid, and light values. Use smooth restrained gradients, approximately 120 degrees, with lighter values toward the upper left; no sharp reflective bands. Ensure the silhouette remains readable on both light and dark Windows backgrounds and remains recognizable at 24 px.

No lettering, no words, no “N”, no “SSH”, no Office-style initial tile, and no celestial or astronomical imagery. One centered icon only, no caption, no mockup, no UI, no icon grid, no multiple alternatives in the same image. Deliver a clean high-resolution RGBA icon with transparent padding around its distinctive silhouette.
```

### 独立负面提示词

如果模型支持 negative prompt，单独填入：

```text
macOS Big Sur icon, iOS icon, rounded-square app tile, squircle, full-canvas backplate, enclosing container, uniform rounded rectangle background, thick extruded slab, inflated 3D blob, glassmorphism, glossy glass, translucent acrylic shell, gel plastic, chrome reflection, specular highlight, bevel, embossed logo, dramatic perspective, isometric view, long cast shadow, neon glow, cyberpunk, excessive gradients, rainbow palette, photorealistic object, detailed server rack, folder badge, lock badge, shield badge, globe badge, Wi-Fi badge, connection cable, network orbit, star, sparkle, flare, comet, planet, galaxy, space imagery, celestial imagery, terminal text, complete command prompt, browser chrome, window control dots, letters, words, Microsoft logo, Windows logo, Office logo, copied Microsoft product icon, icon sheet, multiple icons, mockup, white background
```

### 中文版（适合明确要求中文提示词的模型）

```text
为“FluentShell”设计一个原创的 Windows 产品启动图标。FluentShell 是原生 Windows SSH/SFTP 客户端。只把 FluentShell 当作产品名称，不要把 `Fluent` 翻译成水流、波纹或书写笔触。

唯一核心隐喻是“终端 / Shell”。设计一个统一、可立即识别且剪影独特的终端符号；用 2–3 个宽阔、平坦的重叠形状构成原创轮廓，不要照搬终端窗口截图。最前层可以有一个几何化命令箭头或短光标切口作为小型图形细节，但不能像输入的文字或完整界面。

仅参考所附微软 Fluent 产品图标展示图的视觉语言：熟悉、友好、现代，独特剪影，少量平坦重叠形状，克制的层次，柔和但局部的圆角，精致的蓝—青同类色关系。必须创造原创轮廓，不复制或改造任何微软产品图标和标志。

透明背景，独立自由轮廓，不加外部底板。以 48×48 网格构图，尽量少的形状和转角；48 px 基准下外角约 2 px、内角约 1 px。正视、平坦，仅 2–3 层；阴影只出现在层叠遮挡处且被下层形状裁切。蓝、蔚蓝、青色同类色，包含深中浅色值；平滑克制的约 120° 渐变，左上略亮，不出现强烈反光带。在 Windows 明暗背景都清楚，缩至 24 px 仍能辨认。

禁止文字、字母 N、SSH 字样、Office 式字母角标，以及星星、闪光、轨道、彗星、行星、银河和其他宇宙元素；只输出一个居中的图标，不要标题、界面、样机、图标合集或同图多个方案。输出带透明留白的高清 RGBA 图标。
```

## 为什么这段提示词比“Fluent 2 style icon”更有效

| 容易失控的宽泛词 | 替换为可验证的约束 |
| --- | --- |
| `Fluent 2 style` | 透明自由轮廓、48×48 基准、少形状、局部圆角、正视平层 |
| `3D depth` | 2–3 个平坦重叠层，阴影只区分遮挡关系 |
| `rounded` | 外角约 2 px、内角约 1 px，禁止 full-canvas squircle |
| `vibrant gradient` | 蓝—青同类色，约 120°，左上略亮，过渡少且平滑 |
| `terminal + SSH + SFTP + nova` | 只表达终端 / Shell；产品名与具体功能不逐项画成图形 |
| `Microsoft icon` | 只参考视觉语言，明确原创且不复制微软标志或产品图标 |

## 推荐的生成流程

一次提示模型同时解决隐喻、颜色、材质与小尺寸，容易得到“看起来完成但不够像 Windows”的结果。更可靠的流程是：

1. **先找剪影**：用主提示词生成 4 次，每次只输出一个候选；优先选择缩成黑色剪影后仍独特的方案。
2. **再收层数**：选中方案后要求“保留轮廓，只压缩到 2–3 层，删除所有装饰性小零件”。
3. **再校正底板**：若仍出现 squircle，编辑提示写明“remove the entire enclosing rounded-square plate; preserve only the internal free-standing symbol on alpha transparency”。
4. **最后做小尺寸**：让模型或设计工具分别制作 48、32、24、16 px 版，逐级删细节，不直接缩放；保留 256 px 完整版用于高分辨率场景。

用于二次编辑的短提示词：

```text
Keep the selected FluentShell silhouette and metaphor. Remove the entire enclosing rounded-square/squircle plate and every decorative badge. Place only the free-standing symbol on true alpha transparency. Flatten the icon to 2–3 front-facing overlapping layers, soften only local corners, reduce gradients and shadows, and optimize the result for legibility at 24 px. Do not add or redesign any other element.
```

## 验收清单

- [ ] 去掉颜色、只看黑色剪影时，仍能与常见终端图标区分。
- [ ] 背景真正透明；不存在覆盖大部分 48×48 画布的圆角方形底板。
- [ ] 主隐喻不超过一个，辅助隐喻不超过一个；没有文件夹、锁、服务器、地球等徽章堆叠。
- [ ] 没有因为产品名中的 `Nova` 添加星星、闪光、轨道或其他天体装饰。
- [ ] 无 `N`、`SSH`、单词或仿 Office 字母牌。
- [ ] 仅 2–3 个主要层；正视、平坦，无厚重挤出和斜透视。
- [ ] 圆角细而局部，不把整图变成 squircle。
- [ ] 渐变少、平滑；阴影只解释重叠关系，没有霓虹、镜面、玻璃或长投影。
- [ ] 在浅色和深色背景上，至少一半主体具有清楚对比；形状和隐喻不依赖颜色才能读懂。
- [ ] 48/32/24/16 px 分别检查；小尺寸版本已删去不再可读的细节。
- [ ] 与参考图是“同一设计语言”，但没有复制任何具体微软产品的轮廓、字母或标志。

## 官方来源

- [Fluent 2 Design principles](https://fluent2.microsoft.design/design-principles)
- [Fluent 2 Iconography](https://fluent2.microsoft.design/iconography)
- [Microsoft Learn: App icons](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icons)
- [Microsoft Learn: Design guidelines for Windows app icons](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-design)
- [Microsoft Learn: Construct your Windows app's icon](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-construction)
