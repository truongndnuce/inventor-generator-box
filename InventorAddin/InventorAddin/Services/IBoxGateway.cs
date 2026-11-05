using System.Collections.Generic;
using InventorAddin.Models;

namespace InventorAddin.Services;

/// <summary>Cổng duy nhất để ViewModel làm việc với Inventor. Tất cả lời gọi phải chạy trên UI thread của Inventor.</summary>
public interface IBoxGateway
{
    DocState GetState();

    /// <summary>Đọc giá trị (mm) từ Part đang active.</summary>
    ReadResult ReadValues();

    OperationResult Create(IReadOnlyDictionary<string, double> values, bool allowForeignContent);

    OperationResult Apply(IReadOnlyDictionary<string, double> values);
}
