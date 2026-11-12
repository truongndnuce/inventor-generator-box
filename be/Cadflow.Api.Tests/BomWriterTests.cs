using System.Globalization;
using InventorAddin.Models;

namespace Cadflow.Api.Tests;

public class BomWriterTests
{
    private static BomLine Line(string pn = "BX-1", string desc = "Box", double? mass = 1.5) =>
        new(pn, desc, "job.ipt", "Steel", 1, mass, 250.125, 100, 50.5, 20);

    [Fact]
    public void Header_then_one_row_per_line_with_the_inputs_as_sorted_extra_columns()
    {
        var csv = BomWriter.ToCsv(new[] { Line() }, new Dictionary<string, string> { ["BX_Wall"] = "2", ["BX_Length"] = "100" });
        var rows = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, rows.Length);
        Assert.Equal("Item,PartNumber,Description,FileName,Material,Quantity,MassKg,VolumeCm3,SizeX_mm,SizeY_mm,SizeZ_mm,BX_Length,BX_Wall", rows[0]);
        Assert.Equal("1,BX-1,Box,job.ipt,Steel,1,1.5,250.125,100,50.5,20,100,2", rows[1]);
    }

    [Fact]
    public void An_unknown_measurement_is_an_empty_cell()
    {
        var csv = BomWriter.ToCsv(new[] { Line(mass: null) }, new Dictionary<string, string>());
        Assert.Contains("Steel,1,,250.125", csv);
        Assert.Equal("", BomWriter.Num(double.NaN, 2));
        Assert.Equal("", BomWriter.Num(null, 2));
    }

    [Fact]
    public void Numbers_use_the_invariant_culture()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("vi-VN");   // decimal separator is a comma
            Assert.Equal("1.5", BomWriter.Num(1.5, 4));
            Assert.Contains(",1.5,", BomWriter.ToCsv(new[] { Line() }, new Dictionary<string, string>()));
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("-5", "-5")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Escape_quotes_cells_and_blocks_formulas(string? input, string expected) =>
        Assert.Equal(expected, BomWriter.Escape(input));
}
