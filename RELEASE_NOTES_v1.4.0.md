# v1.4.0 — 图形编辑器 + 3D 视口

这一版把 DSAnimStudio 的两块核心搬进了工具：**图形编辑器**（TAE 事件时间轴 + 参数）
和 **3D 视口**（模型蒙皮 + 骨架预览）。加上原本的动画导出，角色动画页现在能"看—查—改—导"一条龙。

## 新增一：图形编辑器（事件时间轴）

中间点任意一条动画，右侧立刻画出它在 TAE 里的事件：

- 一行 = 一种事件类型（`SetTurnSpeed` / `Blend` / `PlaySound_CenterBody` / `SpawnFFX_ByFloor` …）
- 一根色条 = 一次触发，横向位置就是它在时间轴上的时刻
- **橙色条是音频事件**，青色是其它事件
- 点任意一根条 → 最右「参数」面板列出这条事件的全部字段

顶部三个控件：时间滑块 / 全轨 / 只看音频。

### 参数是真的解出来了

只狼的 TAE 不套模板的话，**所有事件参数都是空的**。这一版读了 DSAnimStudio 的
`TAE.Template.SDT.xml`，把每个事件的参数字段按类型解出来。

| 角色 | 动画 | 事件 | 带参数 |
|---|---|---|---|
| `c0000`（狼） | 1,883 | 70,410 | 68,134 |
| `c1010`（一心） | 500 | 9,809 | 9,477 |

点一条 `PlaySound_CenterBody` 能看到 `SoundType=1  SoundID=101011001`；
点 `CustomLookAtTwistModifier`（DSAnimStudio 截图里那条）能看到
`UpLimitAngle=-1  DownLimitAngle=-1  ModifierID=0  TargetType=3 …`。

### 音频事件能对到具体音轨

`SoundID` 去掉首位 `1` 就是音库内编号，事件名 = 音库ID + 这段数字。
`SoundID 101005000` → `c1010` 音库里的 `c101005000`（实测存在）。

点音频事件时右边出现「音库内编号」和「去「音频导出」找这条」按钮，一键跳过去自动填好搜索词。

## 新增二：3D 视口

点一条动画，视口里直接显示这个角色的模型：拖动旋转、滚轮缩放、滑块拖到任意帧。
「着色 / 线框」可以切换。

底层是新写的 `SkinSolver`，算法**逐字照抄 DSAnimStudio**：

```
skin[n] = inverse(Bind[n]) * Pose[n]
v_out   = Σ w_i * (v_in * skin[nodeIdx_i]) / Σ w_i
```

**关键：`n` 是 FLVER 节点索引（本角色 400 个），不是 hkx 骨骼索引（127 个）。**

这一点搞错就会散架：hkx 的 `bone[2]` 是 `L_Foot_Target1`（对应 FLVER node 359），
而 `node 2` 是 `Pelvis`（承重 19,632 个顶点权重）。拿 hkx 索引直接索引蒙皮矩阵，
Pelvis 的顶点会套上脚的矩阵——这就是之前模型炸成放射状碎片的真正原因。

配套修正：
- 相机改成 **Z-up**（`ChrMesh.Positions` 经 `Axis.ToBlender` 后是 Z 朝上）
- 取景用 80% 分位半径，不用全 bbox（长枪加刀会把范围撑到 2.7 m，人被缩成几十像素）
- 自带 PNG 编码器，视口渲染不依赖任何图形库

实测 `c1010`：81 网格、400 节点、127 骨骼绑定 127/127，帧 0/40/80 都是完整人形。
动画确实在动：相邻 20 帧最大顶点位移 0.04–0.16 m，81 个网格全部有位移，frame 120 精确回到 frame 0。

## 新增三：命令行 `--tae`

```bat
SekiroMapToolkit.exe --cli --tae --root "<解包目录>" --chr c1010 --sound-only
SekiroMapToolkit.exe --cli --tae --root "<解包目录>" --chr c0000 --csv tae.csv
```

| 参数 | 作用 |
|---|---|
| `--chr` | 角色，逗号分隔，省略 = `c0000` |
| `--find` | 只留动画 ID 含这个串的 |
| `--sound-only` | 只打音频事件 |
| `--csv <file>` | 同时写 CSV（角色/动画/type/名字/开始/结束/参数） |

`c0000` 全量导 CSV 是 70,411 行。

## 其它

- 打包内置 `Res\TAE.Template.SDT.xml`，单文件 EXE 开箱就能解事件参数，不用另装 DSAnimStudio
- 截图支持 `--chr c1010` 指定角色
- 版本号 → 1.4.0

## 已知限制

- 少数事件的模板字段数和文件里的字节数对不上（比如 `SetTurnSpeed` 模板 16 字节、文件 8 字节），
  按短的截断，已知字段值仍然正确
- `Blend`（type 16）没参数不是 bug：它在这个模板里本来就没字段
- 视口不做贴图，只按网格色差分色（贴图留给 Blender 里看）
