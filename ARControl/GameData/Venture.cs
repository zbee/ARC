using System.Collections.Generic;
using Dalamud.Data;
using Lumina.Excel.GeneratedSheets;

namespace ARControl.GameData;

internal sealed class Venture
{
    public Venture(DataManager dataManager, RetainerTask retainerTask)
    {
        RowId = retainerTask.RowId;
        Category = retainerTask.ClassJobCategory.Value!;

        var taskDetails = dataManager.GetExcelSheet<RetainerTaskNormal>()!.GetRow(retainerTask.Task)!;
        var taskParameters = retainerTask.RetainerTaskParameter.Value!;
        ItemId = taskDetails.Item.Row;
        Name = taskDetails.Item.Value!.Name.ToString();
        Level = retainerTask.RetainerLevel;
        ItemLevelCombat = retainerTask.RequiredItemLevel;
        RequiredGathering = retainerTask.RequiredGathering;
        Rewards = new List<VentureReward>
        {
            new VentureReward
            {
                Quantity = taskDetails.Quantity[0],
                ItemLevelCombat = 0,
                PerceptionMinerBotanist = 0,
                PerceptionFisher = 0,
            },
            new VentureReward
            {
                Quantity = taskDetails.Quantity[1],
                ItemLevelCombat = taskParameters.ItemLevelDoW[0],
                PerceptionMinerBotanist = taskParameters.PerceptionDoL[0],
                PerceptionFisher = taskParameters.PerceptionFSH[0],
            },
            new VentureReward
            {
                Quantity = taskDetails.Quantity[2],
                ItemLevelCombat = taskParameters.ItemLevelDoW[1],
                PerceptionMinerBotanist = taskParameters.PerceptionDoL[1],
                PerceptionFisher = taskParameters.PerceptionFSH[1],
            },
            new VentureReward
            {
                Quantity = taskDetails.Quantity[3],
                ItemLevelCombat = taskParameters.ItemLevelDoW[2],
                PerceptionMinerBotanist = taskParameters.PerceptionDoL[2],
                PerceptionFisher = taskParameters.PerceptionFSH[2],
            },
            new VentureReward
            {
                Quantity = taskDetails.Quantity[4],
                ItemLevelCombat = taskParameters.ItemLevelDoW[3],
                PerceptionMinerBotanist = taskParameters.PerceptionDoL[3],
                PerceptionFisher = taskParameters.PerceptionFSH[3],
            }
        };
    }

    public uint RowId { get; }
    public ClassJobCategory Category { get; }

    public string? CategoryName
    {
        get
        {
            return Category.RowId switch
            {
                17 => "MIN",
                18 => "BTN",
                19 => "FSH",
                _ => "DoWM",
            };
        }
    }

    public uint ItemId { get; }
    public string Name { get; }
    public byte Level { get; }
    public ushort ItemLevelCombat { get; }
    public ushort RequiredGathering { get; set; }

    public List<VentureReward> Rewards { get; }

    public bool MatchesJob(uint job)
    {
        if (Category.RowId >= 17 && Category.RowId <= 19)
            return Category.RowId == job + 1;
        else
            return job is < 16 or > 18;
    }
}
