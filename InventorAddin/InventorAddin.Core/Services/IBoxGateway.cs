using System.Collections.Generic;
using InventorAddin.Models;

namespace InventorAddin.Services;

/// <summary>The only gateway for the ViewModel to work with Inventor. Every call must run on the Inventor UI thread.</summary>
public interface IBoxGateway
{
    DocState GetState();

    /// <summary>Reads the values (mm) from the active Part.</summary>
    ReadResult ReadValues();

    OperationResult Create(IReadOnlyDictionary<string, double> values, bool allowForeignContent);

    OperationResult Apply(IReadOnlyDictionary<string, double> values);
}
