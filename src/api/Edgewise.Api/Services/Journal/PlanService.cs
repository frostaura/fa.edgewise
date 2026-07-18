using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Services.Journal;

/// <summary>Application service for trade plans, templates and position sizing.</summary>
public sealed class PlanService(EdgewiseDbContext db, ICurrentUser currentUser, BucketEquityService bucketEquity)
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

    /// <summary>Suggested size differing from the actual by more than this fraction flags an override.</summary>
    public const decimal OverrideTolerance = 0.02m;

    // ------------------------------------------------------------- sizing

    public async Task<SizePreviewDto> SizePreviewAsync(
        Guid bucketId, decimal stopPrice, decimal entryPrice, Guid? riskProfileId, CancellationToken ct)
    {
        if (entryPrice <= 0 || stopPrice <= 0)
        {
            throw ApiException.BadRequest("invalid_prices", "entryPrice and stopPrice must be positive.");
        }

        var profile = await ResolveRiskProfileAsync(riskProfileId, ct);
        var equityMinor = await bucketEquity.GetEquityMinorAsync(bucketId, ct);
        return ComputeSizePreview(equityMinor, profile.RiskPct, entryPrice, stopPrice);
    }

    /// <summary>
    /// Pure sizing rule: risk budget = equity x riskPct (riskPct in percent points, e.g. 1 = 1%);
    /// suggested qty = risk budget / |entry - stop|.
    /// </summary>
    public static SizePreviewDto ComputeSizePreview(long equityMinor, decimal riskPct, decimal entryPrice, decimal stopPrice)
    {
        var rValueMinor = (long)decimal.Round(equityMinor * riskPct / 100m, 0, MidpointRounding.AwayFromZero);
        var distance = Math.Abs(entryPrice - stopPrice);
        var suggestedQty = distance <= 0m ? 0m : decimal.Round(JournalCommon.ToMajor(rValueMinor) / distance, 8);
        var notionalMinor = JournalCommon.ToMinor(suggestedQty * entryPrice);
        return new SizePreviewDto(suggestedQty, rValueMinor, notionalMinor, equityMinor, riskPct);
    }

    // -------------------------------------------------------------- plans

    public async Task<List<PlanDto>> ListAsync(TradePlanStatus? status, CancellationToken ct)
    {
        var query = db.TradePlans.AsNoTracking();
        if (status is not null)
        {
            query = query.Where(p => p.Status == status);
        }

        var plans = await query.OrderByDescending(p => p.CreatedAt).Take(200).ToListAsync(ct);
        var instruments = await InstrumentsByIdAsync(plans.Select(p => p.InstrumentId), ct);
        return plans.Select(p => JournalCommon.ToDto(p, instruments.GetValueOrDefault(p.InstrumentId))).ToList();
    }

    public async Task<PlanDto> GetAsync(Guid id, CancellationToken ct)
    {
        var plan = await db.TradePlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw ApiException.NotFound("plan_not_found", "Plan not found.");
        var instrument = await db.Instruments.AsNoTracking().FirstOrDefaultAsync(i => i.Id == plan.InstrumentId, ct);
        return JournalCommon.ToDto(plan, instrument);
    }

    public async Task<PlanDto> CreateAsync(CreatePlanRequest request, CancellationToken ct)
    {
        var userId = JournalCommon.RequireUserId(currentUser);
        if (request.StopPrice <= 0)
        {
            throw ApiException.BadRequest("invalid_stop", "stopPrice must be positive.");
        }

        _ = await db.Instruments.FirstOrDefaultAsync(i => i.Id == request.InstrumentId, ct)
            ?? throw ApiException.BadRequest("instrument_not_found", "Instrument not found.");
        _ = await db.Buckets.FirstOrDefaultAsync(b => b.Id == request.BucketId, ct)
            ?? throw ApiException.BadRequest("bucket_not_found", "Bucket not found.");
        var profile = await ResolveRiskProfileAsync(request.RiskProfileId, ct);

        // Server-side size suggestion (requires an entry price estimate to be meaningful).
        var sizeQty = request.SizeQty ?? 0m;
        var overridden = request.SizeOverridden ?? false;
        if (request.EntryPrice is decimal entryPrice && entryPrice > 0)
        {
            var equityMinor = await bucketEquity.GetEquityMinorAsync(request.BucketId, ct);
            var preview = ComputeSizePreview(equityMinor, profile.RiskPct, entryPrice, request.StopPrice);
            if (request.SizeQty is null || request.SizeQty <= 0)
            {
                sizeQty = preview.SuggestedQty;
            }
            else if (preview.SuggestedQty > 0)
            {
                var deviation = Math.Abs(request.SizeQty.Value - preview.SuggestedQty) / preview.SuggestedQty;
                overridden = overridden || deviation > OverrideTolerance;
            }
        }

        var plan = new TradePlan
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TemplateId = request.TemplateId,
            InstrumentId = request.InstrumentId,
            BucketId = request.BucketId,
            RiskProfileId = profile.Id,
            Direction = request.Direction,
            SetupTag = request.SetupTag,
            TriggerText = request.TriggerText,
            StopPrice = request.StopPrice,
            TargetRuleJson = request.TargetRuleJson,
            SizeQty = sizeQty,
            SizeOverridden = overridden,
            InvalidationNote = request.InvalidationNote,
            IsPaper = request.IsPaper,
            Status = TradePlanStatus.Active,
            CockpitCheckJson = request.CockpitCheckJson,
            ChecklistConfirmedJson = request.ChecklistConfirmedJson,
            CreatedAt = DateTime.UtcNow,
        };
        db.TradePlans.Add(plan);
        db.TradePlanVersions.Add(NewVersion(plan, 1));
        await db.SaveChangesAsync(ct);

        var instrument = await db.Instruments.AsNoTracking().FirstOrDefaultAsync(i => i.Id == plan.InstrumentId, ct);
        return JournalCommon.ToDto(plan, instrument);
    }

    public async Task<PlanDto> PatchAsync(Guid id, PatchPlanRequest request, CancellationToken ct)
    {
        var plan = await db.TradePlans.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw ApiException.NotFound("plan_not_found", "Plan not found.");
        if (plan.Status is TradePlanStatus.Cancelled or TradePlanStatus.Expired)
        {
            throw ApiException.Conflict("plan_not_editable", "Cancelled or expired plans cannot be edited.");
        }

        var coreChanged = false;
        if (request.Direction is not null && request.Direction != plan.Direction)
        {
            plan.Direction = request.Direction.Value;
            coreChanged = true;
        }

        if (request.SetupTag is not null && request.SetupTag != plan.SetupTag)
        {
            plan.SetupTag = request.SetupTag;
            coreChanged = true;
        }

        if (request.TriggerText is not null && request.TriggerText != plan.TriggerText)
        {
            plan.TriggerText = request.TriggerText;
            coreChanged = true;
        }

        if (request.StopPrice is not null && request.StopPrice != plan.StopPrice)
        {
            if (request.StopPrice <= 0)
            {
                throw ApiException.BadRequest("invalid_stop", "stopPrice must be positive.");
            }

            plan.StopPrice = request.StopPrice.Value;
            coreChanged = true;
        }

        if (request.TargetRuleJson is not null && request.TargetRuleJson != plan.TargetRuleJson)
        {
            plan.TargetRuleJson = request.TargetRuleJson;
            coreChanged = true;
        }

        if (request.SizeQty is not null && request.SizeQty != plan.SizeQty)
        {
            plan.SizeQty = request.SizeQty.Value;
            plan.SizeOverridden = true;
            coreChanged = true;
        }

        if (request.InvalidationNote is not null && request.InvalidationNote != plan.InvalidationNote)
        {
            plan.InvalidationNote = request.InvalidationNote;
            coreChanged = true;
        }

        if (request.ChecklistConfirmedJson is not null)
        {
            plan.ChecklistConfirmedJson = request.ChecklistConfirmedJson;
        }

        if (request.Status is TradePlanStatus.Draft or TradePlanStatus.Active)
        {
            plan.Status = request.Status.Value;
        }

        if (coreChanged)
        {
            var lastVersion = await db.TradePlanVersions
                .Where(v => v.PlanId == plan.Id)
                .MaxAsync(v => (int?)v.Version, ct) ?? 0;
            db.TradePlanVersions.Add(NewVersion(plan, lastVersion + 1));
        }

        await db.SaveChangesAsync(ct);
        var instrument = await db.Instruments.AsNoTracking().FirstOrDefaultAsync(i => i.Id == plan.InstrumentId, ct);
        return JournalCommon.ToDto(plan, instrument);
    }

    public async Task<PlanDto> CancelAsync(Guid id, CancellationToken ct)
    {
        var plan = await db.TradePlans.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw ApiException.NotFound("plan_not_found", "Plan not found.");
        if (plan.Status == TradePlanStatus.Promoted)
        {
            throw ApiException.Conflict("plan_promoted", "A promoted plan cannot be cancelled.");
        }

        plan.Status = TradePlanStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        return JournalCommon.ToDto(plan);
    }

    /// <summary>
    /// Promotes a plan into a live trade: either links an existing trade or creates a new
    /// Open trade from the supplied fills (OpenedAt = first fill).
    /// </summary>
    public async Task<TradeDto> PromoteAsync(Guid id, PromotePlanRequest request, CancellationToken ct)
    {
        var userId = JournalCommon.RequireUserId(currentUser);
        var plan = await db.TradePlans.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw ApiException.NotFound("plan_not_found", "Plan not found.");
        if (plan.Status is TradePlanStatus.Cancelled or TradePlanStatus.Expired or TradePlanStatus.Promoted)
        {
            throw ApiException.Conflict("plan_not_promotable", $"Plan status is {plan.Status}.");
        }

        Trade trade;
        if (request.TradeId is Guid tradeId)
        {
            trade = await db.Trades.FirstOrDefaultAsync(t => t.Id == tradeId, ct)
                ?? throw ApiException.NotFound("trade_not_found", "Trade not found.");
            trade.PlanId = plan.Id;
        }
        else
        {
            var fills = request.Fills;
            if (fills is null || fills.Count == 0)
            {
                throw ApiException.BadRequest("fills_required", "Provide tradeId or at least one fill.");
            }

            var bucket = await db.Buckets.FirstAsync(b => b.Id == plan.BucketId, ct);
            var entrySide = plan.Direction == TradeDirection.Long ? FillSide.Buy : FillSide.Sell;
            var entries = fills.Where(f => f.Side == entrySide).ToList();
            if (entries.Count == 0)
            {
                throw ApiException.BadRequest("no_entry_fills", "At least one fill must open in the plan's direction.");
            }

            var qty = entries.Sum(f => f.Qty);
            var avgEntry = entries.Sum(f => f.Qty * f.Price) / qty;
            var openedAt = JournalCommon.AsUtc(fills.Min(f => f.At));

            trade = new Trade
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PlanId = plan.Id,
                InstrumentId = plan.InstrumentId,
                BucketId = plan.BucketId,
                AccountId = fills.Select(f => f.AccountId).FirstOrDefault(a => a is not null),
                Direction = plan.Direction,
                Status = TradeStatus.Open,
                OpenedAt = openedAt,
                Qty = qty,
                AvgEntryPrice = avgEntry,
                FeesMinor = fills.Sum(f => f.FeeMinor),
                Currency = bucket.Currency,
                RPlanned = ComputePlannedR(plan, avgEntry),
                EmotionTag = EmotionTag.None,
                IsPaper = plan.IsPaper,
                PositionMethod = PositionMethod.Manual,
            };
            db.Trades.Add(trade);

            for (var i = 0; i < fills.Count; i++)
            {
                db.Fills.Add(NewManualFill(userId, fills[i], plan.InstrumentId, trade.Id, bucket.Currency, i));
            }
        }

        plan.Status = TradePlanStatus.Promoted;
        await db.SaveChangesAsync(ct);
        var instrument = await db.Instruments.AsNoTracking().FirstOrDefaultAsync(i => i.Id == trade.InstrumentId, ct);
        return JournalCommon.ToDto(trade, instrument);
    }

    // ---------------------------------------------------------- templates

    public async Task<List<PlanTemplateDto>> ListTemplatesAsync(CancellationToken ct)
    {
        var templates = await db.PlaybookTemplates.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
        return templates
            .Select(t => new PlanTemplateDto(t.Id, t.UserId, t.Name, t.Description, t.PrefillJson, t.ChecklistJson, t.UserId is null))
            .ToList();
    }

    public async Task<PlanTemplateDto> CreateTemplateAsync(UpsertTemplateRequest request, CancellationToken ct)
    {
        var userId = JournalCommon.RequireUserId(currentUser);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw ApiException.BadRequest("invalid_name", "Template name is required.");
        }

        var template = new PlaybookTemplate
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = request.Name.Trim(),
            Description = request.Description,
            PrefillJson = request.PrefillJson,
            ChecklistJson = request.ChecklistJson,
        };
        db.PlaybookTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        return new PlanTemplateDto(template.Id, template.UserId, template.Name, template.Description,
            template.PrefillJson, template.ChecklistJson, false);
    }

    public async Task<PlanTemplateDto> PatchTemplateAsync(Guid id, UpsertTemplateRequest request, CancellationToken ct)
    {
        var template = await RequireOwnTemplateAsync(id, ct);
        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            template.Name = request.Name.Trim();
        }

        template.Description = request.Description ?? template.Description;
        template.PrefillJson = request.PrefillJson ?? template.PrefillJson;
        template.ChecklistJson = request.ChecklistJson ?? template.ChecklistJson;
        await db.SaveChangesAsync(ct);
        return new PlanTemplateDto(template.Id, template.UserId, template.Name, template.Description,
            template.PrefillJson, template.ChecklistJson, false);
    }

    public async Task DeleteTemplateAsync(Guid id, CancellationToken ct)
    {
        var template = await RequireOwnTemplateAsync(id, ct);
        db.PlaybookTemplates.Remove(template);
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------ lookups

    public async Task<PlanLookupsDto> LookupsAsync(CancellationToken ct)
    {
        var templates = await ListTemplatesAsync(ct);
        var instruments = await db.Instruments.AsNoTracking()
            .OrderBy(i => i.Symbol)
            .Take(500)
            .ToListAsync(ct);
        var buckets = await db.Buckets.AsNoTracking().OrderBy(b => b.Name).ToListAsync(ct);
        var profiles = await db.RiskProfiles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);

        return new PlanLookupsDto(
            templates,
            instruments.Select(JournalCommon.ToSummary).ToList(),
            buckets.Select(b => new BucketSummaryDto(b.Id, b.Name, b.Kind, b.Currency)).ToList(),
            profiles.Select(r => new RiskProfileSummaryDto(r.Id, r.Name, r.RiskPct, r.HeatCapPct, r.IsActive)).ToList());
    }

    /// <summary>Fallback instrument creation for symbols the catalogue does not know yet.</summary>
    public async Task<InstrumentSummaryDto> CreateInstrumentAsync(CreateInstrumentRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Symbol))
        {
            throw ApiException.BadRequest("invalid_symbol", "Symbol is required.");
        }

        var symbol = request.Symbol.Trim().ToUpperInvariant();
        var existing = await db.Instruments.FirstOrDefaultAsync(i => i.Symbol == symbol && i.Exchange == null, ct);
        if (existing is not null)
        {
            return JournalCommon.ToSummary(existing);
        }

        var currency = request.Currency?.Trim().ToUpperInvariant()
            ?? await db.Users.Select(u => u.BaseCurrency).FirstOrDefaultAsync(ct) ?? "USD";
        var instrument = new Instrument
        {
            Id = Guid.NewGuid(),
            Symbol = symbol,
            Name = string.IsNullOrWhiteSpace(request.Name) ? symbol : request.Name.Trim(),
            AssetClass = request.AssetClass ?? AssetClass.Custom,
            Exchange = null,
            Currency = currency,
            ProviderSymbolsJson = "{}",
        };
        db.Instruments.Add(instrument);
        await db.SaveChangesAsync(ct);
        return JournalCommon.ToSummary(instrument);
    }

    // ------------------------------------------------------------ helpers

    internal static Fill NewManualFill(
        Guid userId, PromoteFillRequest fill, Guid instrumentId, Guid? tradeId, string currency, int ordinal = 0)
    {
        var at = JournalCommon.AsUtc(fill.At);
        return new Fill
        {
            Id = Guid.NewGuid(),
            AccountId = fill.AccountId ?? Guid.Empty,
            UserId = userId,
            InstrumentId = instrumentId,
            TradeId = tradeId,
            Side = fill.Side,
            Qty = fill.Qty,
            Price = fill.Price,
            FeeMinor = fill.FeeMinor,
            FeeCurrency = currency,
            At = at,
            Source = FillSource.Manual,
            SourceHash = ManualSourceHash(userId, instrumentId, fill.Side, fill.Qty, fill.Price, at, ordinal),
            MatchStatus = tradeId is null ? MatchStatus.Proposed : MatchStatus.Matched,
        };
    }

    /// <summary>Deterministic hash so an identical manual fill cannot be double-logged.</summary>
    internal static string ManualSourceHash(
        Guid userId, Guid instrumentId, FillSide side, decimal qty, decimal price, DateTime at, int ordinal = 0)
    {
        var payload = $"manual|{userId}|{instrumentId}|{side}|{qty:0.##########}|{price:0.##########}|{at:O}|{ordinal}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    internal static decimal? ComputePlannedR(TradePlan plan, decimal avgEntry)
    {
        // Best-effort: derive a target from targetRule rMultiple levels; otherwise null.
        if (string.IsNullOrWhiteSpace(plan.TargetRuleJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(plan.TargetRuleJson);
            if (doc.RootElement.TryGetProperty("levels", out var levels) && levels.ValueKind == JsonValueKind.Array)
            {
                decimal? maxR = null;
                foreach (var level in levels.EnumerateArray())
                {
                    if (level.TryGetProperty("atR", out var atR) && atR.TryGetDecimal(out var r))
                    {
                        maxR = maxR is null ? r : Math.Max(maxR.Value, r);
                    }
                }

                return maxR;
            }
        }
        catch (JsonException)
        {
            // Malformed target rule: no planned R.
        }

        return null;
    }

    private TradePlanVersion NewVersion(TradePlan plan, int version) => new()
    {
        Id = Guid.NewGuid(),
        PlanId = plan.Id,
        Plan = plan,
        Version = version,
        FieldsJson = JsonSerializer.Serialize(new
        {
            direction = plan.Direction.ToString().ToLowerInvariant(),
            setupTag = plan.SetupTag,
            triggerText = plan.TriggerText,
            stopPrice = plan.StopPrice,
            targetRuleJson = plan.TargetRuleJson,
            sizeQty = plan.SizeQty,
            invalidationNote = plan.InvalidationNote,
        }, SnapshotJson),
        At = DateTime.UtcNow,
    };

    private async Task<RiskProfile> ResolveRiskProfileAsync(Guid? riskProfileId, CancellationToken ct)
    {
        if (riskProfileId is Guid id)
        {
            return await db.RiskProfiles.FirstOrDefaultAsync(r => r.Id == id, ct)
                ?? throw ApiException.BadRequest("risk_profile_not_found", "Risk profile not found.");
        }

        return await db.RiskProfiles.OrderByDescending(r => r.IsActive).ThenBy(r => r.Name).FirstOrDefaultAsync(ct)
            ?? throw ApiException.BadRequest("no_risk_profile", "No risk profile configured.");
    }

    private async Task<PlaybookTemplate> RequireOwnTemplateAsync(Guid id, CancellationToken ct)
    {
        var template = await db.PlaybookTemplates.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw ApiException.NotFound("template_not_found", "Template not found.");
        if (template.UserId is null)
        {
            throw ApiException.Forbidden("template_shipped", "Shipped templates cannot be modified.");
        }

        return template;
    }

    private async Task<Dictionary<Guid, Instrument>> InstrumentsByIdAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var idSet = ids.Distinct().ToList();
        return await db.Instruments.AsNoTracking()
            .Where(i => idSet.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, ct);
    }
}
