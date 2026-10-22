namespace VeilDesk.Core.Detection;

/// <summary>
/// Regions that the bundled NudeNet 320n model genuinely predicts, plus the
/// whole-person and dedicated YuNet face targets. Values 0..17 preserve
/// NudeNet's class order exactly; Face makes no gender or identity claim.
/// </summary>
public enum BodyRegion
{
    FemaleGenitaliaCovered = 0,
    FaceFemale = 1,
    ButtocksExposed = 2,
    FemaleBreastExposed = 3,
    FemaleGenitaliaExposed = 4,
    MaleBreastExposed = 5,
    AnusExposed = 6,
    FeetExposed = 7,
    BellyCovered = 8,
    FeetCovered = 9,
    ArmpitsCovered = 10,
    ArmpitsExposed = 11,
    FaceMale = 12,
    BellyExposed = 13,
    MaleGenitaliaExposed = 14,
    AnusCovered = 15,
    FemaleBreastCovered = 16,
    ButtocksCovered = 17,
    Person = 100,
    Face = 101
}

public static class BodyRegionExtensions
{
    private static readonly string[] NudeNetLabels =
    [
        "FEMALE_GENITALIA_COVERED",
        "FACE_FEMALE",
        "BUTTOCKS_EXPOSED",
        "FEMALE_BREAST_EXPOSED",
        "FEMALE_GENITALIA_EXPOSED",
        "MALE_BREAST_EXPOSED",
        "ANUS_EXPOSED",
        "FEET_EXPOSED",
        "BELLY_COVERED",
        "FEET_COVERED",
        "ARMPITS_COVERED",
        "ARMPITS_EXPOSED",
        "FACE_MALE",
        "BELLY_EXPOSED",
        "MALE_GENITALIA_EXPOSED",
        "ANUS_COVERED",
        "FEMALE_BREAST_COVERED",
        "BUTTOCKS_COVERED"
    ];

    public static string ToDisplayName(this BodyRegion region) => region switch
    {
        BodyRegion.FemaleGenitaliaCovered => "Female genitalia (covered)",
        BodyRegion.FaceFemale => "Female face",
        BodyRegion.ButtocksExposed => "Buttocks (exposed)",
        BodyRegion.FemaleBreastExposed => "Female breasts (exposed)",
        BodyRegion.FemaleGenitaliaExposed => "Female genitalia (exposed)",
        BodyRegion.MaleBreastExposed => "Male chest",
        BodyRegion.AnusExposed => "Anus (exposed)",
        BodyRegion.FeetExposed => "Feet (exposed)",
        BodyRegion.BellyCovered => "Belly (covered)",
        BodyRegion.FeetCovered => "Feet (covered)",
        BodyRegion.ArmpitsCovered => "Armpits (covered)",
        BodyRegion.ArmpitsExposed => "Armpits (exposed)",
        BodyRegion.FaceMale => "Male face",
        BodyRegion.BellyExposed => "Belly (exposed)",
        BodyRegion.MaleGenitaliaExposed => "Male genitalia (exposed)",
        BodyRegion.AnusCovered => "Anus (covered)",
        BodyRegion.FemaleBreastCovered => "Female breasts (covered)",
        BodyRegion.ButtocksCovered => "Buttocks (covered)",
        BodyRegion.Person => "Whole person",
        BodyRegion.Face => "Face (YuNet · all people)",
        _ => region.ToString()
    };

    public static string ToNudeNetLabel(this BodyRegion region)
    {
        var index = (int)region;
        return index >= 0 && index < NudeNetLabels.Length ? NudeNetLabels[index] : region.ToString();
    }

    public static bool IsExplicitExposed(this BodyRegion region) => region is
        BodyRegion.FemaleGenitaliaExposed or
        BodyRegion.MaleGenitaliaExposed or
        BodyRegion.AnusExposed or
        BodyRegion.ButtocksExposed or
        BodyRegion.FemaleBreastExposed;

    public static bool IsGenderFemale(this BodyRegion region) => region is
        BodyRegion.FemaleBreastExposed or
        BodyRegion.FemaleBreastCovered or
        BodyRegion.FemaleGenitaliaExposed or
        BodyRegion.FemaleGenitaliaCovered or
        BodyRegion.FaceFemale;

    public static bool IsGenderMale(this BodyRegion region) => region is
        BodyRegion.MaleGenitaliaExposed or
        BodyRegion.MaleBreastExposed or
        BodyRegion.FaceMale;

    public static IEnumerable<BodyRegion> AllDetectableRegions()
        => new[] { BodyRegion.Face }.Concat(Enumerable.Range(0, 18).Select(i => (BodyRegion)i));
}
