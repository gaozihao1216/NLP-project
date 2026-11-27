# Unity / Blender 表现层缺口问题清单

> 生成日期：2026-11-26 ｜ 2026-11-26 下午：表现层代码已落地（见文末「落地记录」），涉及条目转 ◐
> 依据：对 `game/`、`docs/`、`content/` 的实际盘点 + `vertical-slice-plan.md`、`系统架构设计.md`、`architecture.md` 中已定方案
> 状态图例：☐ 未开始 ｜ ◐ 代码已写/待 Unity 编辑器验证 ｜ ☑ 已完成
> 分工口径：A = 高子豪（`com.starpawn.combat`），B = 张容博（presentation、Assets/Game、资产装配）

---

## 0. 总判断

战斗仿真侧（三维运动、扫掠命中、判面护盾、能源、快照契约）已跑通且测试全绿；
**表现层为完全空白**：`com.starpawn.presentation` 0 行源码、全仓库 0 个场景 / 0 个 prefab / 0 个模型文件、
Unity 工程从未被编辑器打开（`ProjectSettings/` 只有 `ProjectVersion.txt`，manifest 只有两个非渲染包）。
快照端数据（位置 / Yaw-Pitch-Roll / 速度 / 瞄准点 / `BattleEvent` 事件流）已备齐，消费端无人接。

---

## 一、光栅化显示管线（优先级 P0–P1）

| 编号 | 问题 | 现状 | 缺口 | 负责 | 优先级 | 验收标准 | 状态 |
|---|---|---|---|---|---|---|---|
| R1 | Unity 工程未初始化 | `ProjectSettings/` 仅 1 个文件；`manifest.json` 无渲染相关包 | 用 2021.3 打开 `game/`，让编辑器生成完整 ProjectSettings；顺手决定是否引入 URP | B | **P0** | 工程可打开、可进入 Play 模式 | ☐ |
| R2 | 无任何场景 | 计划中的 `BattleSandbox.unity` / `StrategicSandbox.unity` 不存在 | 建两个沙盒场景；战斗场景为垂直切片主舞台 | B | **P0** | 场景存在且可切换 | ☐ |
| R3 | presentation 包零源码 | `Runtime/` 只有 asmdef | 建 `BattleSceneController / BattleInputController / BattleViewRegistry / ShipView / ProjectileView / ObstacleView / BattleHud` | B | **P0** | 7 个类落位，编译通过 | ☐ |
| R4 | 战斗循环未接线 | 无人调用 `CampaignFacade.StepBattle()` | `FixedUpdate` 以 1/30s 固定步长推进仿真；`Update` 读 `ReadSnapshot()` 刷新画面；插值留到第二版 | B | **P0** | Play 模式下仿真随帧推进，画面随快照变化 | ☐ |
| R5 | 实体映射缺失 | 无 | `EntityId → View` 注册表：快照出现新实体创建 View，死亡/消失隐藏并播效果 | B | **P0** | 快照内每艘舰/每发弹在场景中有对应物体 | ☐ |
| R6 | 装配入口缺失 | `GameBootstrap.Create()` 只被 CLI 调用 | 场景内一个 Bootstrap 组件调用 `GameBootstrap.Create(contentRoot)` 并把 facade 交给表现层 | B | **P0** | 打开场景按 Play 即进入战斗，无需改代码 | ☐ |
| R7 | 相机与比例尺换算未实现 | 架构文档定"比例尺随缩放动态换算，纯表现"，无实现 | 追舰第三人称相机 + km→Unity 单位的统一缩放因子（与 M3 联动） | B | P1 | 视角跟随玩家舰，缩放时画面比例一致 | ☐ |
| R8 | 环境渲染缺失 | 仿真里的陨石只有半径/血量数据 | 星空天空盒、星点粒子、恒星发光球+点光源+光晕、程序化陨石群视觉层（用 `RandomSeed` 复现） | B | P1 | 画面有星空舞台，陨石可见且与仿真位置一致 | ☐ |
| R9 | 特效缺失 | 无 | 尾焰、炮口焰、命中火花（Particle System 即可）；驱动源是 `BattleEvent` 流 | B | P1 | 命中/开火事件触发对应特效 | ☐ |
| R10 | HUD 缺失 | `combat-controls.md` 已定口径，画不出 | 舰体/护盾扇面三档亮度、装填、能量三档+当前加速度、速度、准星与瞄准点 | B | P1 | 灰盒条件下玩家能读出"为什么打不出去/为什么没推力" | ☐ |
| R11 | 渲染管线选型未定 | 未装 URP，也未声明用内置管线 | 拍板：内置管线（最快出灰盒）或 URP（后期效果空间大）；定下后写进决策记录 | A+B 共同 | P1 | 决策记录存在，工程配置一致 | ☐ |

---

## 二、碰撞模型（优先级 P1，仿真侧为主）

| 编号 | 问题 | 现状 | 缺口 | 负责 | 优先级 | 验收标准 | 状态 |
|---|---|---|---|---|---|---|---|
| C1 | 视觉模型与碰撞球不对齐 | 命中判定是纯球（`hullRadiusKm` 0.20–0.37） | 约定"模型包围盒 ↔ 仿真半径"对齐规则：模型外观以舰长为特征长度，半径取包络球近似；上舰模后灰盒复测命中率不突变 | A+B 共同 | P1 | 换上真舰模后单局 TTK 与灰盒一致（±15% 内） | ☐ |
| C2 | 舰-陨石碰撞缺失 | 陨石只挡炮弹（弹丸扫掠先判陨石）；舰体飞进陨石无任何处理，直接穿模 | `MovementSystem` 增加舰体 vs 陨石球的推开/停挡规则（至少：不可穿越，贴面滑行或反弹） | A | P1 | 玩家舰撞陨石不穿模，行为可预期 | ☐ |
| C3 | 舰-舰碰撞缺失 | 无任何处理 | 同 C2，舰体 vs 舰体（含友军）。最低版本：不可穿越 | A | P1 | 两舰对冲不重叠 | ☐ |
| C4 | 部位判定几何升级（延后） | 球模型无法支撑船头/船身分区 | 记录：若未来做部位损坏/跳弹（已在延后清单），命中几何需升 capsule/OBB，判面逻辑从"三分量点乘"改几何查询 | A | P2（暂缓） | 决策记录中留档即可 | ☐ |

> 说明：Unity PhysX 有意被绕开（仿真自算物理、固定步长保证确定性），这是既定正确设计。
> C2/C3 也要在仿真侧做，**不是**加 Collider 组件。

---

## 三、战舰 3D 建模与资产（优先级 P2，第一版用 Primitive 顶着）

| 编号 | 问题 | 现状 | 缺口 | 负责 | 优先级 | 验收标准 | 状态 |
|---|---|---|---|---|---|---|---|
| M1 | 无任何 Blender 资产 | 全仓库 0 个 `.blend/.fbx/.gltf` 文件；proposal 写明 Blender 制作导出 glTF/FBX | 最小集合：一套共用舰模（低模）、陨石低模、天空盒/星点。垂直切片方案明确"不要等 Blender 资产"，故排在 View 层之后 | B | P2 | 模型进工程，替换 Cube 仍可完整打一局 | ☐ |
| M2 | 挂点规范未定义 | 仿真只给舰体位置+姿态；炮口位置、引擎喷口位置在仿真里不存在 | 模型上定义挂点（muzzle、engine、hit 根节点），命名规范写进资产清单；特效与炮口焰依赖它 | A+B 共同 | P2 | 特效从挂点发射，位置随姿态正确 | ☐ |
| M3 | 单位/缩放换算未定 | 仿真单位 km，模型通常米级；R7 依赖此决策 | 定统一缩放因子与换算常量落点（建议放 Foundation 或 ViewRegistry 一处，不散落） | A+B 共同 | P2 | 一处定义，全部 View 复用 | ☐ |
| M4 | 导出与导入管线约定未定 | 无 | glTF vs FBX 选型、轴向/缩放导出参数、命名与目录规范（如 `Assets/Game/Art/Ships/`）、导入设置 | B | P2 | 有一份资产导入规范文档，模型按它入库 | ☐ |
| M5 | 舰种区分方案未落 | 设计已定：共用模型 + 涂装/缩放/武器挂载区分 | 落成实现：涂装贴图 ×N、缩放表、挂载件预制体；至少覆盖巡逻艇/驱逐舰/巡洋舰三型 | B | P2 | 三型舰在场景中肉眼可辨 | ☐ |

---

## 四、工程与流程（跨三个方面）

| 编号 | 问题 | 现状 | 缺口 | 负责 | 优先级 | 验收标准 | 状态 |
|---|---|---|---|---|---|---|---|
| G1 | content 目录在 Unity 中的位置未定 | CLI 从 cwd 向上找 `content/ships`；打包后路径不存在 | 决定 StreamingAssets / Addressables / 编辑器内直读方案；`GameBootstrap.Create(contentRoot)` 的实参来源 | B | P1 | 打包后的 build 也能进战斗（或明确第一版仅编辑器内运行并记录） | ☐ |
| G2 | 表现层无法在本机验证运行的兜底 | Smart App Control 历史问题已解决（测试可跑），但 Unity 编辑器行为未验证过 | 在允许跑 Unity 的机器上完成 R1–R6 后录 30–60s 视频（垂直切片首个里程碑） | B | P0 验收件 | 视频存在：方块舰转向、开火、击毁、结算 | ☐ |
| G3 | 比例尺动态换算决策无责任人 | 架构文档一句话，无实现无归属 | 并入 R7 一并落地 | B | P1 | 同 R7 | ☐ |

---

## 五、建议执行顺序

1. **P0（打通第一帧）**：R1 → R2 → R6 → R3 → R4 → R5。目标是灰盒 Cube 在 Unity 里随仿真动起来。
2. **P1（可玩可读）**：R7–R10、C1–C3、G1。目标是完整打完一局且 HUD 可读、碰撞不穿模。
3. **P2（看着像）**：M1–M5、C4。目标是用真模型替换 Primitive，涂装区分舰种。

> 按既定分工，本清单约 85% 条目落在 B（张容博）名下；A 名下的是 C2/C3 两条仿真侧碰撞规则。
> 这与 vertical-slice-plan 的两人边界表一致，不需要重新切分。

---

## 落地记录（2026-11-26 下午）

表现层灰盒管线已写完并通过离线编译验证（22 个 BuildCheck 工程 0 错误，其中 Presentation/
Bootstrap 首次含真源码；冒烟测试 134 条全绿）：

| 文件 | 内容 |
| --- | --- |
| `presentation/Runtime/Battle/BattleSceneController.cs` | 总控：FixedUpdate 1/30s 推进仿真→读快照→排事件；Update 同步 View、追舰相机、胜负与结算、Enter 重开 |
| `presentation/Runtime/Battle/BattleViewRegistry.cs` | EntityId→View 映射与生命周期；BattleEvent→特效（Fire/Hit/Critical/Ricochet/Destroyed） |
| `presentation/Runtime/Battle/ShipView.cs` | 灰盒舰：Box+舰首标+逐炮座视觉炮塔（按 Turrets 快照转 localRotation、装填压暗） |
| `presentation/Runtime/Battle/ProjectileView.cs` / `ObstacleView.cs` | 曳光弹（按帧位移拉长）；陨石（半径取快照、血量调暗、ID 派生固定姿态） |
| `presentation/Runtime/Battle/BattleFx.cs` | 程序化闪光池（无粒子资产） |
| `presentation/Runtime/Battle/BattleInputController.cs` | 键位→命令：W/S 档位、AD 偏航、RF 俯仰、QE 滚转、T 循环锁定、G 飞辅、Space/左键开火、1/2/3 能量档 |
| `presentation/Runtime/Battle/BattleHud.cs` | OnGUI HUD：血/盾/能/装填条、炮塔计数、三面 DR 亮度、提前量准星、目标框+距离、拒绝命令回显、胜负横幅 |
| `presentation/Runtime/Environment/StarfieldBuilder.cs` | 程序化星空：运行时逐像素生成全景天空盒（银盘星带+1600 星+40 亮星）+恒星定向光+深空环境光 |
| `presentation/Runtime/SimMath.cs` | 1 单位 = 1 km 口径 + 仿真→Unity 坐标换算 |
| `Assets/Game/Bootstrap/BattleDemoLauncher.cs` | 组合根：`[RuntimeInitializeOnLoadMethod]` 零场景启动，自动找 content/ 并调 `GameBootstrap.Create` |
| `presentation/Editor/BattleSceneBuilder.cs` | Editor 菜单 StarPawn/构建战斗沙盒场景，生成 `Assets/Game/Scenes/BattleSandbox.unity` |
| `tools/BuildCheck/UnityEngine.CoreModule/` | UnityEngine 离线编译桩（只服务 BuildCheck，不进 Unity 工程） |

**验证状态与边界**：
- 编译验证走的是 UnityEngine **桩**（类型/签名按 2021.3 抄写），桩通过 ≠ 编辑器内通过；Editor 菜单脚本未离线编译。
- 尚未在 Unity 2021.3 中实际打开（R1 未完成），打开后需确认：Play 即见星空+灰盒战斗、`Skybox/Panoramic` 存在、content/ 自动发现成功。
- 因此 R2–R6 标 ◐（代码就位、编辑器验证待做）；R7–R10 的相机变焦/特效/HUD 已随本轮实现大半，验证后可批量转 ☑。

---

银橙AI出品，官网 https://yinchengai.cn/，反馈邮箱：yincheng@yinchengai.cn，微信公众号：银橙AI。
