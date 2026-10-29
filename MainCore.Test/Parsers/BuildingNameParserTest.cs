using MainCore.Enums;
using MainCore.Parsers;

namespace MainCore.Test.Parsers
{
    public class BuildingNameParserTest
    {
        [Theory]
        [InlineData("Hero'sMansion", BuildingEnums.HerosMansion)]
        [InlineData("Hero's Mansion", BuildingEnums.HerosMansion)]
        [InlineData("Hero’s mansion", BuildingEnums.HerosMansion)]
        [InlineData("HerosMansion", BuildingEnums.HerosMansion)]
        [InlineData("  HERO'S\u00a0MANSION  ", BuildingEnums.HerosMansion)]
        [InlineData("Stonemason's Lodge", BuildingEnums.StonemasonsLodge)]
        [InlineData("Main building", BuildingEnums.MainBuilding)]
        [InlineData("Woodcutter", BuildingEnums.Woodcutter)]
        public void ParsesEnglishDisplayNames(string name, BuildingEnums expected)
        {
            BuildingNameParser.TryParse(name, out var type).ShouldBeTrue();
            type.ShouldBe(expected);
            BuildingNameParser.Parse(name).ShouldBe(expected);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("37")]
        [InlineData("Unknown building")]
        [InlineData("Heldenhof")]
        public void RejectsUnrecognizedNames(string? name)
        {
            BuildingNameParser.TryParse(name, out _).ShouldBeFalse();
        }

        [Fact]
        public void RejectsUnrecognizedNameWhenParsing()
        {
            Should.Throw<ArgumentException>(() => BuildingNameParser.Parse("Unknown building"));
        }

        [Fact]
        public void ParsesEveryExistingEnumName()
        {
            foreach (var type in Enum.GetValues<BuildingEnums>())
                BuildingNameParser.Parse(type.ToString()).ShouldBe(type);
        }
    }
}
