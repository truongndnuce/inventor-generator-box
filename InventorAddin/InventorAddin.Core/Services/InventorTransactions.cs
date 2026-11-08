using System;
using System.Collections.Generic;
using InventorAddin.Host;
using InventorAddin.Models;
using Inv = Inventor;

namespace InventorAddin.Services;

/// <summary>Chạy một thao tác ghi trong một Transaction: lỗi hoặc model có feature hỏng thì hoàn tác toàn bộ.</summary>
internal static class InventorTransactions
{
    public static OperationResult Run(Inv.Application app, Inv.PartDocument part, string name, Action work, string okMessage)
    {
        var tx = app.TransactionManager.StartTransaction((Inv._Document)part, name);
        var wasSilent = app.SilentOperation;
        app.SilentOperation = true;
        try
        {
            work();
            part.Update();
            var broken = FindBrokenFeature(part.ComponentDefinition);
            if (broken != null) throw new InvalidOperationException(broken);
            tx.End();
            Log.Info($"{name}: OK");
            return new OperationResult(true, okMessage);
        }
        catch (Exception ex)
        {
            Log.Error($"{name} failed, rolling back", ex);
            try { tx.Abort(); } catch (Exception abortEx) { Log.Error("Abort failed", abortEx); }
            return new OperationResult(false, $"{name} failed; the model was left unchanged. Reason: {ex.Message}");
        }
        finally
        {
            app.SilentOperation = wasSilent;
        }
    }

    private static readonly HashSet<Inv.HealthStatusEnum> BadHealth = new()
    {
        Inv.HealthStatusEnum.kInErrorHealth,
        Inv.HealthStatusEnum.kInconsistentHealth,
        Inv.HealthStatusEnum.kCannotComputeHealth,
        Inv.HealthStatusEnum.kDriverLostHealth,
        Inv.HealthStatusEnum.kInvalidLimitsHealth,
        Inv.HealthStatusEnum.kDeletedHealth,
    };

    private static string? FindBrokenFeature(Inv.PartComponentDefinition def)
    {
        foreach (Inv.PartFeature f in def.Features)
            if (BadHealth.Contains(f.HealthStatus))
                return $"Feature '{f.Name}' is broken ({f.HealthStatus}).";
        return null;
    }
}
