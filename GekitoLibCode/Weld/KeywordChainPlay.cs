using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GekitoLib.Keywords;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace GekitoLib.Weld;

/// <summary>
/// 关键词连锁打出引擎。通过 <see cref="Register"/> 注册关键词后，
/// 打出一张带该关键词的牌时，会依次自动打出持有者指定牌堆中所有其他带该关键词的牌。
/// 连锁打出的牌带防重入标记，其打出不再触发连锁，避免无限循环；
/// 其他效果（AutoPlay 等）打出的同关键词牌仍可正常触发连锁。
/// 激类库内置注册「焊接」（<see cref="GekitoLibKeywords.Weld"/>），其他 mod 可直接使用该关键词，
/// 也可注册自己的连锁关键词。
/// </summary>
public static class KeywordChainPlay
{
    private static readonly Dictionary<CardKeyword, PileType[]> Registered = new();
    private static readonly List<Func<CardModel, bool, IEnumerable<CardKeyword>>> VirtualKeywordProviders = new();
    private static bool _builtinRegistered;

    /// <summary>注册连锁关键词。piles 缺省为手牌/弃牌堆/抽牌堆。</summary>
    public static void Register(CardKeyword keyword, params PileType[] piles)
    {
        Registered[keyword] = piles is { Length: > 0 } ? piles : [PileType.Hand, PileType.Discard, PileType.Draw];
    }

    /// <summary>
    /// 惰性注册内置连锁关键词。必须在首次检测（Postfix）时调用：
    /// <see cref="GekitoLibKeywords.Weld"/> 是 BaseLib 的 [CustomEnum] 字段，
    /// 其真实枚举值由 BaseLib 在 ModelDb.Init 时注入（GenEnumValues，Prefix）——
    /// 若在 mod Initialize 阶段（注入前）读取该字段，拿到的是占位值 default(CardKeyword)=0，
    /// 用它作注册表 key 会导致运行时「真实值 ≠ key」永远匹配不上（连锁全失效，物理/虚拟皆然）。
    /// 首次 Postfix 时字段必已注入，此时读取并注册才是正确值。
    /// </summary>
    private static void EnsureBuiltinRegistered()
    {
        if (_builtinRegistered)
        {
            return;
        }
        _builtinRegistered = true;
        Registered.TryAdd(GekitoLibKeywords.Weld, [PileType.Hand, PileType.Discard, PileType.Draw]);
    }

    /// <summary>
    /// 注册「虚拟连锁关键词提供者」：为满足条件的牌提供临时的连锁关键词集合，
    /// 仅用于本次打出时的连锁检测与候选匹配，不写入卡牌 Keywords 状态。
    /// 取代「临时 ApplyKeyword + 检测后 RemoveKeyword」的做法：
    ///  - 无关键词状态污染（异常/中断也不会残留，不会触发卡面刷新）
    ///  - 无清理时机问题（不依赖 Harmony Postfix 相对顺序）
    /// 提供者在连锁检测时（OnPlayWrapper 的 Postfix）同步求值，须为纯函数（幂等、无副作用）。
    /// 第二个参数 <paramref name="isAutoPlay"/> 表示该牌是否由效果自动打出（AutoPlay/连锁）；
    /// 需要「仅手动打出触发」的提供者必须用它短路（等价于旧方案 Prefix 的 isAutoPlay 检查）。
    /// </summary>
    /// <example>
    /// 某 Power 让每回合前 N 张手动打出的牌触发焊接连锁：
    /// <code>
    /// KeywordChainPlay.RegisterVirtualKeyword(static (card, isAutoPlay) =>
    ///     !isAutoPlay &amp;&amp; card.Owner?.Creature?.GetPower&lt;WeldingPower&gt;() is { } power &amp;&amp; power.ShouldProvideVirtualWeld(card)
    ///         ? [GekitoLibKeywords.Weld] : []);
    /// </code>
    /// </example>
    public static void RegisterVirtualKeyword(Func<CardModel, bool, IEnumerable<CardKeyword>> provider)
    {
        VirtualKeywordProviders.Add(provider);
    }

    /// <summary>该牌本次打出实际生效的连锁关键词条目 = 物理关键词 ∪ 虚拟关键词提供者返回的集合。</summary>
    private static IEnumerable<KeyValuePair<CardKeyword, PileType[]>> EffectiveChains(CardModel card, bool isAutoPlay)
    {
        var effectiveKeywords = card.Keywords
            .Concat(VirtualKeywordProviders.SelectMany(provider => provider(card, isAutoPlay) ?? []));
        return Registered.Where(pair => effectiveKeywords.Contains(pair.Key));
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.OnPlayWrapper))]
    private static class ChainPatch
    {
        private static readonly HashSet<CardModel> ChainingCards = [];

        [HarmonyPostfix]
        public static void Postfix(ref Task __result, CardModel __instance, PlayerChoiceContext choiceContext, Creature? target, bool isAutoPlay)
        {
            if (ChainingCards.Contains(__instance)) return;
            EnsureBuiltinRegistered();

            // 在打出效果执行前快照连锁候选：「为手牌添加关键词」这类效果在 OnPlay 中才生效，
            // 若等原任务完成再扫描，刚被添加关键词的牌会被立刻连锁打出；
            // 且逐堆延迟扫描会让连锁中被打进弃牌堆的牌再次被枚举到（同一张牌打两次）。
            var effective = EffectiveChains(__instance, isAutoPlay).ToList();
            if (effective.Count == 0) return;
            var candidates = effective
                .SelectMany(pair => pair.Value.SelectMany(pileType => pileType.GetPile(__instance.Owner).Cards)
                    .Where(c => c != __instance && c.Keywords.Contains(pair.Key)))
                .Distinct()
                .ToList();
            if (candidates.Count == 0) return;
            __result = Chain(__result, candidates, choiceContext, target);
        }

        private static async Task Chain(Task original, List<CardModel> candidates, PlayerChoiceContext choiceContext, Creature? target)
        {
            await original;

            foreach (var card in candidates)
            {
                // 连锁途中卡牌可能被其他效果移动，打出前确认仍在可连锁的牌堆
                if (card.Pile == null) continue;
                if (card.Pile.Type != PileType.Hand && card.Pile.Type != PileType.Discard && card.Pile.Type != PileType.Draw) continue;
                await PlayCardViaChain(choiceContext, card, target);
            }
        }

        private static async Task PlayCardViaChain(PlayerChoiceContext choiceContext, CardModel card, Creature? target)
        {
            var validTarget = target is { IsAlive: true } ? target : null;

            ChainingCards.Add(card);
            try
            {
                if (card.EnergyCost.CostsX || card.HasStarCostX)
                {
                    await card.SpendResources();
                    await CardCmd.AutoPlay(choiceContext, card, validTarget, skipXCapture: true);
                }
                else
                {
                    await CardCmd.AutoPlay(choiceContext, card, validTarget);
                }
            }
            finally
            {
                ChainingCards.Remove(card);
            }
        }
    }
}
