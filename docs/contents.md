# GekitoLib 内容文档

激类库（GekitoLib）是 Slay the Spire 2 的共享机制库 Mod，把多个角色 Mod 会重复实现的通用机制收敛到同一处实现、同一命名空间（`GekitoLib.*`）。使用方 Mod 在自己的 `.json` 清单里声明依赖：

```json
"dependencies": [
  {"id": "BaseLib", "min_version": "3.3.8"},
  {"id": "GekitoLib", "min_version": "0.1.0"}
]
```

并在 csproj 中引用游戏 mods 目录下的 `GekitoLib.dll`（`<Private>false</Private>`，不要随自己 Mod 重复分发 dll）。

当前包含以下机制：

## 1. 焊接连锁（Weld Chain） — `GekitoLib.Weld`

**机制**：打出一张带「焊接」关键词的牌时，依次自动打出持有者手牌、弃牌堆、抽牌堆中所有其他焊接牌。

**组成**：

| 成员 | 说明 |
|------|------|
| `GekitoLibKeywords.Weld` | 共享焊接关键词（`[CustomEnum]` 注入，所有 Mod 共用同一实例，跨 Mod 的焊接牌可互相连锁；定义于 `GekitoLib.Keywords`）。本地化 key：`GEKITOLIB-WELD` |
| `KeywordChainPlay` | 连锁引擎。内置已注册 Weld；其他 Mod 可用 `KeywordChainPlay.Register(keyword, piles...)` 注册自己的连锁关键词（piles 缺省为 手牌/弃牌堆/抽牌堆）；可用 `KeywordChainPlay.RegisterVirtualKeyword(provider)` 注册「虚拟连锁关键词」让指定牌无需物理关键词即可触发连锁（见下） |
| `DaoGuan`（`GekitoLib.Enchantments`） | 导管附魔：为攻击/技能牌添加「焊接」关键词。本地化 key：`GEKITOLIB-DAO_GUAN` |

**实现要点**（改动机制前必读）：
- 连锁候选在打出的 `OnPlayWrapper` **执行前**快照（Postfix 里同步取候选列表，再把连锁续接到原 Task 之后）——防止「打出时为其他牌添加焊接」的效果导致刚加焊接的牌被立刻连锁，也防止连锁途中被打进弃牌堆的牌被二次枚举。
- 连锁打出的牌带防重入标记，其打出不再触发连锁；其他效果（AutoPlay 等）打出的焊接牌仍可正常触发连锁。
- X 费牌（能量 X 或辉星 X）连锁时先 `SpendResources()` 再 `AutoPlay(skipXCapture: true)`。
- 原目标已死时以 null 目标 AutoPlay。
- **虚拟连锁关键词**（`RegisterVirtualKeyword`）：为满足条件的牌提供临时的连锁关键词集合，**不写入卡牌 Keywords 状态**，取代「临时 `ApplyKeyword` + 检测后 `RemoveKeyword`」的做法。收益：①异常/中断也不会残留关键词；②不触发 `CardCmd.ApplyKeyword/RemoveKeyword` 的卡面刷新（`NCard.UpdateVisuals`），避免多人选择界面挂载期间节点脱离树引发原版 reparent 崩溃；③不依赖 Harmony Postfix 相对顺序。提供者签名 `(CardModel, bool isAutoPlay)`——需要「仅手动打出触发」时必须用 `!isAutoPlay` 短路（等价旧方案 Prefix 的检查）；提供者须为纯函数（幂等、无副作用），在连锁检测时同步求值；触发判定与候选匹配统一按「物理 ∪ 虚拟」关键词计算。
- ⚠️ **CustomEnum 注入时序陷阱（实测必踩）**：`GekitoLibKeywords.Weld` 是 BaseLib `[CustomEnum]` 静态字段，真实枚举值由 BaseLib 在 **ModelDb.Init 时**注入（`GenEnumValues` Prefix）——**晚于所有 mod 的 Initialize**。任何在 Initialize/静态构造阶段读取该字段拿到的都是占位值 `default(CardKeyword)=0`；若用它作字典 key（如 `Register(GekitoLibKeywords.Weld)`）缓存下来，运行时卡牌/提供者返回的「注入后真实值」将永远匹配不上 → **连锁全部静默失效**（物理/虚拟皆然，且无任何报错）。因此内置 Weld 采用**惰性注册**（`EnsureBuiltinRegistered`，首次 Postfix 时才读字段注册）；使用方 `Register(keyword, ...)` 自定义连锁关键词时，同样必须保证调用发生在 ModelDb.Init（注入完成）之后。

**典型用法**：

```csharp
// 卡牌效果：为一张牌添加「焊接」
CardCmd.ApplyKeyword(card, GekitoLibKeywords.Weld);

// Power：让「每回合前 N 张手动打出的牌」触发焊接连锁（不写卡牌状态，无需清理）
KeywordChainPlay.RegisterVirtualKeyword(static (card, isAutoPlay) =>
    !isAutoPlay && card.Owner?.Creature?.GetPower<WeldingPower>() is { } power && power.ShouldProvideVirtualWeld(card)
        ? [GekitoLibKeywords.Weld] : []);

// 遗物：从牌组选牌附魔「导管」
var enchantment = ModelDb.Enchantment<DaoGuan>();
foreach (var card in await CardSelectCmd.FromDeckForEnchantment(Owner, enchantment, 1, prefs))
{
    CardCmd.Enchant(enchantment.ToMutable(), card, 1);
    CardCmd.Preview(card);
}
```

## 2. 互斥姿态框架（Stance Framework） — `GekitoLib.Stances`

**机制**：参考 StS1 观者的姿态系统。姿态以「姿态能力」（PowerModel 子类）为载体，所有已注册姿态互斥——同一时刻至多处于一种姿态；重复进入当前姿态不触发任何效果；切换到另一姿态时先退出旧姿态（触发退出钩子）再进入新姿态。

**组成**：

| 成员 | 说明 |
|------|------|
| `StanceRegistry.Register<TPower>(tint, onEnterFeedback)` | 在使用方 Mod 的 ModInitializer 中注册姿态。`tint` 为进入姿态时的角色身体染色（可空）；`onEnterFeedback` 为进入反馈回调（音效/特效，可空）。重复注册同一类型被忽略 |
| `StanceCmd.EnterStance<TPower>(choiceContext, creature)` | 进入姿态。未注册的类型抛 `InvalidOperationException` |
| `StanceCmd.ExitStance(creature)` | 退出当前姿态（若有） |
| `IAfterEnterStance` / `IAfterExitStance` | 进出姿态钩子。遗物/能力/卡牌实现后由框架回调（进入钩子额外遍历 抽牌堆/手牌/弃牌堆/消耗堆 中的卡牌）。回调参数 `stancePower` 为姿态能力实例，用 `is not MyStancePower` 模式匹配判断具体姿态 |
| `StanceTint` | 生物身体贴图染色工具（要求战斗形象由 BaseLib `NCreatureVisualsFactory` 从单张 Texture2D 构建，子节点 "Visuals" 为身体 Sprite2D）。一般无需直接调用，注册时给 `tint` 即可 |

**使用流程**：

```csharp
// 1. 定义姿态能力（StackType.Single）
public class MyStancePower : CustomPowerModel { /* Type/StackType/图标/本地化由你的 Mod 提供 */ }

// 2. 在你的 ModInitializer 中注册（身体染色 + 进入音效均可选）
StanceRegistry.Register<MyStancePower>(new Color("b3d1ff"), () => myEnterSfx.Play());

// 3. 卡牌/遗物效果中进入姿态
await StanceCmd.EnterStance<MyStancePower>(choiceContext, Owner.Creature);

// 4. 「每当进入某姿态时…」的效果：遗物/能力/卡牌实现钩子接口
public async Task AfterEnterStance(PlayerChoiceContext choiceContext, Creature creature, PowerModel stancePower)
{
    if (stancePower is not MyStancePower) return;
    // …
}
```

**注意**：姿态能力的本地化（title/description/smartDescription）与图标由使用方 Mod 自己提供，框架不含任何具体姿态。

## 3. 临时属性能力 — `GekitoLib.Powers.TemporaryStatPower<TStatPower>`

**机制**：能力存续期间给予宿主等量的另一属性能力（力量/敏捷等），本能力被移除时等额回收（`AfterApplied`/`AfterRemoved` 对称结算，`silent: true` 不播图标弹跳）。叠层/减层时内部属性随层数差值实时同步，移除时按当前层数回收，不会出现内部属性越过 0 翻转。

- 默认临时属性数值 = 本能力层数（`Amount`）；需与层数解耦时重写 `protected virtual decimal TempAmount`（如改用 `DynamicVars`）。
- 派生类需要额外的进入/退出效果时重写 `AfterApplied`/`AfterRemoved` 并调用 `base.`。
- 图标路径等 `CustomPowerModel` 成员由派生类自行重写（本基类不管美术）。

**实现要点**（改动机制前必读）：
- 叠层同步在 `AfterPowerAmountChanged` 中完成，两个过滤条件缺一不可：
  - `power != this`：内部 `TStatPower` 的 Apply 也会触发全局 `Hook.AfterPowerAmountChanged`，必须过滤，防止内部属性变化引发连锁误同步（死循环）。
  - `amount == Amount`：首次应用时 `AfterApplied` 已同步过，随后钩子会带相同的量再触发一次，跳过可防止双重结算。
- 参考实现：原版 `TemporaryStrengthPower` / BaseLib `CustomTemporaryPowerModel` 的 `AfterPowerAmountChanged` 均为同款「差值同步 + 双条件过滤」模式。
- `StackType` 使用方通常设为 `Counter`（叠层才有意义）；`Single` 等不叠层的用法不受此同步影响。

**典型用法**：

```csharp
// 存续期间 +1 敏捷、移除时收回（数值 = 层数）
public class MyGuardPower : TemporaryStatPower<DexterityPower>
{
    public override PowerType Type => PowerType.Buff;
    public override PowerStackType StackType => PowerStackType.Single;
}

// 数值与层数解耦（层数恒为 1，数值来自 DynamicVars），且附加额外结算
public class MyRagePower : TemporaryStatPower<StrengthPower>
{
    protected override decimal TempAmount => DynamicVars[nameof(StrengthPower)].BaseValue;

    public override async Task AfterApplied(Creature? applier, CardModel? cardSource)
    {
        await base.AfterApplied(applier, cardSource);
        // 额外效果…
    }
}
```

## 4. 沉底词条 — `GekitoLib.Keywords`

**机制**：带 `GekitoLibKeywords.Bottom` 关键词的卡牌在战斗开始（初始洗牌）时被放到初始抽牌堆底部。使用方式与焊接等关键词完全一致——在卡牌 `CanonicalKeywords` 中显式声明即可，**无需实现接口或覆写基类**；行为由 `BottomPlaceCardPatches` 以 Harmony Postfix 全局实现。

**组成**：

| 成员 | 说明 |
|------|------|
| `GekitoLibKeywords.Bottom` | 共享「沉底」关键词（`[CustomEnum]` 注入，所有 Mod 共用同一实例）。本地化 key：`GEKITOLIB-BOTTOM` |
| `BottomPlaceCardPatches` | 全局实现：`ModifyShuffleOrder` Postfix（检测卡牌带 Bottom 关键词则初始洗牌沉底，幂等） |

**实现要点**（改动机制前必读）：
- Postfix 是**追加式**（不跳过原方法），不覆盖使用方自身的 `ModifyShuffleOrder` 覆写逻辑；`cards.Remove` 失败（已被使用方逻辑移走）则静默跳过，幂等。
- 沉底语义限定「初始洗牌」（`isInitialShuffle == true`），战斗中洗牌不受影响。
- 关键词位置为 `AutoKeywordPosition.After`；从旧 Mod 迁入时卡牌关键词 ID 会变化，旧存档兼容性按「通用注意事项」处理。

**典型用法**：

```csharp
// 使用方卡牌：在 CanonicalKeywords 中显式声明（与焊接一致）
public sealed class MyRitualCard : MyBaseCard
{
    public override IEnumerable<CardKeyword> CanonicalKeywords => [GekitoLibKeywords.Bottom];
}
```

## 5. 尸爆术 — `GekitoLib.Powers.CorpseExplosionPower`

**机制**：持有者死亡时，对所有其他敌人造成「持有者最大生命值 × 层数」的伤害（参考 StS1 的 Corpse Explosion）。

**组成**：

| 成员 | 说明 |
|------|------|
| `CorpseExplosionPower` | Debuff / Counter。`AfterDeath` 中结算，伤害 `ValueProp.Unpowered`（不吃力量等修正）。本地化 key：`GEKITOLIB-CORPSE_EXPLOSION_POWER` |

**实现要点**：
- `wasRemovalPrevented` 为 true（死亡被阻止，如重生）时不触发；目标排除持有者自身且仅存活敌人。
- 图标：`images/powers/corpseexplosionpower.png`（小）+ `big/`（大）。

**典型用法**：

```csharp
await PowerCmd.Apply<CorpseExplosionPower>(choiceContext, target, stacks, Owner.Creature, this);
```

## 6. 多层护甲 — `GekitoLib.Powers.LayeredArmorPower`

**机制**：StS1 的 Plated Armor——回合结束时获得「层数」点格挡；受到未被格挡的攻击伤害后层数 -1（不会像 StS2 镀层那样每回合衰减）。

**组成**：

| 成员 | 说明 |
|------|------|
| `LayeredArmorPower` | Buff / Counter。`BeforeSideTurnEndEarly` 获得格挡、`AfterDamageReceived` 未格挡攻击伤害后 `Decrement`。本地化 key：`GEKITOLIB-LAYERED_ARMOR_POWER` |

**实现要点**：
- 只对 `ValueProp.Move` 的攻击伤害（且非自身/非无来源）触发减层；`result.UnblockedDamage <= 0` 不触发。
- 自带 StS1 风格音效（`audio/powers/layered_armor.ogg`）与格挡 HoverTip。
- 图标：`images/powers/layeredarmor.png`（小）+ `big/`（大）。

**典型用法**：

```csharp
await PowerCmd.Apply<LayeredArmorPower>(choiceContext, Owner.Creature, stacks, Owner.Creature, this);
```

## 7. 色彩哲学家候选池 — `GekitoLib.Events`

**机制**：把 mod 自己的卡池加进**色彩哲学家**（Colorful Philosophers）事件的可选池。原版 `CardPoolColorOrder` 是硬编码 5 个原版池的表达式属性，无法反射注入，故用 Postfix 追加。

**组成**：

| 成员 | 说明 |
|------|------|
| `ColorfulPhilosophersPools.AddPool(Type)` / `AddPool<TPool>()` | 注册候选池（按类型、重复注册忽略）。使用方还要在自己的本地化提供事件选项文案，key 为两条带后缀的完整 key：`COLORFUL_PHILOSOPHERS.pages.INITIAL.options.<EnergyColorName 大写>.title` 与 `.description`（见下方 key 结构） |
| `ColorfulPhilosophersPatch`（`GekitoLib.Patches`） | Postfix `get_CardPoolColorOrder`，把已注册的池追加进返回列表（幂等） |

**实现要点**（改动机制前必读）：
- 注册表存**类型**而非模型实例，Postfix 触发时（游戏运行中）才经 `ModelDb` 解析成卡池实例：ModInitializer 阶段模型尚未注册进 ModelDb，此时查 `ModelDb.CardPool<T>()` 会抛 `ModelNotFoundException`（实测曾致 mod 初始化中断、事件选项与后续注册全部失效）。
- **事件选项本地化 key 结构（实测，缺一即 NRE 卡死）**：`EnergyColorName` 由 BaseLib `CustomCardPoolModel` 给出，等于 `Id.Category + "∴" + Id.Entry`——分隔符是**单个 `∴`（U+2234）**，不是西里尔字母、不是 ASCII `-`；`Category` 是卡池类型 slug（如 `CARD_POOL`），`Entry` 是 BaseLib 给 mod 卡池加 modId 前缀后的 slug（如 `JTYSMOD2-Y_Y60_CARD_POOL`），全部大写。最终 key 示例：`COLORFUL_PHILOSOPHERS.pages.INITIAL.options.CARD_POOL∴JTYSMOD2-Y_Y60_CARD_POOL.title` 与 `.description`。原版 `EventOption` 构造器只查 `key + ".title"` / `key + ".description"`（`LocString.GetIfExists`），缺后缀、分隔符/前缀任一不匹配 → 返回 null → `AddDetailsTo(null)` NRE → **事件初始化中断卡死**（影响所有角色的该事件，不止 mod 角色）。排查 key 不匹配最快路径是加诊断日志打印运行时实际 `EnergyColorName`，不要靠猜。

**典型用法**：

```csharp
// ModInitializer 中：注册自己的卡池（每池一个选项，EnergyColorName 决定本地化 key）。
ColorfulPhilosophersPools.AddPool<MyCardPool>();
```

## 8. 初始卡/初始遗物升级链 — `GekitoLib.StarterUpgrades`

**机制**：把 mod 的「初始卡/初始遗物 → 升级版」替换关系注入原版的**先古之齿**（ArchaicTooth）与**奥罗巴斯之触**（TouchOfOrobas）两个古遗物机制。两者映射字典都是每次访问新建的表达式属性，无法反射注入，故用 Postfix getter 就地加条目。

**组成**：

| 成员 | 说明 |
|------|------|
| `StarterUpgradeRegistry.RegisterCardUpgrade(Type, Type)` / `RegisterCardUpgrade<TStarter, TUpgraded>()` | 注册初始卡 → 先古卡替换（ArchaicTooth） |
| `StarterUpgradeRegistry.RegisterRelicUpgrade(Type, Type)` / `RegisterRelicUpgrade<TStarter, TUpgraded>()` | 注册初始遗物 → 升级遗物替换（TouchOfOrobas） |
| `ArchaicToothPatch` / `TouchOfOrobasPatch`（`GekitoLib.Patches`） | 分别 Postfix 原版两个映射，注入注册表内容 |

**实现要点**（改动机制前必读）：
- 注册表存**类型**而非模型实例，Postfix 触发时（游戏运行中）才经 `ModelDb` 解析成模型：ModInitializer 阶段模型尚未注册进 ModelDb，此时查 `ModelDb.Card<T>()` / `ModelDb.Relic<T>()` 会抛异常（实测曾致 mod 初始化中断、升级链全部失效、奥罗巴斯之触回退原版默认头环 Circlet）。
- 初始卡的**升级状态继承是原版行为、无需注册方处理**：`ArchaicTooth.GetTranscendenceTransformedCard` 内部会按初始卡是否已升级对新卡 `CardCmd.Upgrade` 并拷贝附魔，本库只注入映射字典，继承自动生效（见 src 源码）。
- 必须注入**字典本体**而不是只 patch 替换取值方法：`GetTranscendenceStarterCard` 靠字典 key 识别可替换的初始牌（否则 `SetupForPlayer` 返回 false、古遗物事件不提供给养）、`TranscendenceCards` 同时决定 DustyTome 的排除名单（先古卡只能经古遗物替换获得，不会被尘封典籍直接发）。

**典型用法**：

```csharp
// ModInitializer 中：
StarterUpgradeRegistry.RegisterCardUpgrade<MyStarterCard, MyAncientCard>();
StarterUpgradeRegistry.RegisterRelicUpgrade<MyStarterRelic, MyUpgradedRelic>();
```

## 9. 星图标换皮框架 — `GekitoLib.UI`

**机制**：把某 mod 的**辉星**（Stars）视觉整体换成自定义贴图（仅视觉替换，底层仍复用游戏内置 Stars 资源，未注册 mod / 原版内容保持原版四芒星）。归属判定基于「模型/角色类型所在程序集」——注册时传本 mod 任意类型，则该程序集定义的所有模型（卡/遗物/能力）与角色自动套用。

**组成**：

| 成员 | 说明 |
|------|------|
| `StarIconSkin` | 皮肤定义：大图标（计数器/卡牌星费）、文本内联图标、闪光/旋转层（可空）、数字描边色（可空）、计数器悬停标题/描述（可空 = 保留原版） |
| `StarIconSkins.Register<TMarker>(skin)` / `Register(assembly, skin)` | 注册一套皮肤（重复注册同一程序集视为覆盖） |
| `StarIconSkins` 内全部 patch（`GekitoLib.Patches`） | 卡牌星费图标+描边、左下计数器（Initialize + Activate 双钩子）、多人玩家状态条、`{Stars:starIcons()}` 格式化器、卡/遗物/能力描述内单星图标替换 |

**实现要点**（改动机制前必读）：
- 判定归属：`模型类型.Assembly == 注册程序集`（涵盖注册到原版池的 mod 选项卡）。计数器按 `player.Character` 类型程序集判定角色归属。
- **统计器兜底钩子**：`NStarCounter.Initialize` Postfix + `NCombatUi.Activate` Postfix 双钩子换皮。Activate 在 Initialize 之后还会建能量计数器/Reparent 星计数器，只挂 Initialize 会被冲掉（图标回退原版），Activate 钩子必须保留。
- 文本图标两条路：`{Stars:starIcons()}` 走 `StarIconsFormatter` Prefix；`{Amount:starIcons()}` 裸值无 owner，靠 Power 的 HoverTips/GetDumbHoverTip 挂静态上下文。`{singleStarIcon}` 是各模型 Add 进 LocString 的变量，卡/遗物/能力各 patch 重 Add。
- 跳过原版 formatter 时必须显式 `__result = true`，否则 SmartFormat 视为无 formatter 受理而整段回退原文。
- 计数器悬停文案只在皮肤提供了 title+description 时才覆盖，否则保留原版「Stars」语义。

**典型用法**：

```csharp
// ModInitializer 中：
StarIconSkins.Register<MyMainFile>(new StarIconSkin
{
    BigIconPath = "charui/big_star.png".ImagePath(),
    TextIconPath = "charui/text_star.png".ImagePath(),
    LabelOutlineColor = new Color("6E4412FF"),
    CounterHoverTitle = new LocString("static_hover_tips", "MYMOD-STAR_COUNT.title"),
    CounterHoverDescription = new LocString("static_hover_tips", "MYMOD-STAR_COUNT.description"),
});
```

## 通用注意事项

- **ID 稳定性**：关键词/附魔的本地化 key 以 `GEKITOLIB-` 为前缀，是所有使用方 Mod 共用的公共资源，改名即破坏性变更。从别的 Mod 迁入本库的内容其 ID 会变化，旧存档中已附魔的卡牌会失效——EA 阶段可接受，发布说明需注明。
- **加载顺序**：使用方 Mod 声明 `dependencies` 后，游戏保证 GekitoLib 先于其初始化（`StanceRegistry.Register` / 各注册表在自己 Mod 的 ModInitializer 中调用是安全的）。
- **构建顺序**：本库 dll 输出到游戏 `mods/GekitoLib/`；使用方 Mod 通过 HintPath 引用该 dll，因此**必须先构建 GekitoLib 再构建使用方 Mod**。
- 本库的 Harmony patch：`KeywordChainPlay` 内部的 `CardModel.OnPlayWrapper` Postfix、`BottomPlaceCardPatches`（`GekitoLib.Patches`）的 `CardModel.ModifyShuffleOrder` Postfix、`ColorfulPhilosophersPatch`/`ArchaicToothPatch`/`TouchOfOrobasPatch` 与 `StarIconSkins` 内各 patch，均为追加式或短路式，与使用方 Mod 自身的 patch 无冲突；星图标 formatter 同时只可能与使用方同类 patch 冲突，此时后 PatchAll 的覆盖先者（使用方通常在 GekitoLib 之后初始化）。
