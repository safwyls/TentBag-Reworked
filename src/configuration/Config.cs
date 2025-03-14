using System.Diagnostics.CodeAnalysis;

namespace tentbag.configuration;

[SuppressMessage("ReSharper", "AutoPropertyCanBeMadeGetOnly.Global")]
public class Config {
    // Maximum allowed radius and height for a tent
    public int MaxRadius { get; set; } = 3;
    public int MaxHeight { get; set; } = 7;

    // Build Effort is calculated per solid block, so a 3x3x7 tent would have a maximum possible BuildEffort of 3*3*3*7 = 189
    public float BuildEffort { get; set; } = 3F;

    // Misc Preferences
    public bool RequireFloor { get; set; } = false;
    public bool ReplacePlantsAndRocks { get; set; } = true;
    public bool PutTentInInventoryOnUse { get; set; } = true;
    public string HighlightErrorColor { get; set; } = "#2FFF0000";

    public string[] BannedBlocks { get; set; } = {
        "game:log-grown-*",
        "game:log-resin-*",
        "game:log-resinharvested-*",
        "game:statictranslocator-*",
        "game:teleporterbase",
        "game:crop-*",
        "game:herb-*",
        "game:mushroom-*",
        "game:smallberrybush-*",
        "game:bigberrybush-*",
        "game:water-*",
        "game:lava-*",
        "game:farmland-*",
        "game:rawclay-*",
        "game:peat-*",
        "game:rock-*",
        "game:ore-*",
        "game:crock-burned-*",
        "game:bowl-meal",
        "game:claypot-cooked",
        "game:anvil-*",
        "game:forge"
    };
}
