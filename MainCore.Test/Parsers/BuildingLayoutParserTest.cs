using MainCore.DTO;
using MainCore.Entities;
using MainCore.Enums;
using MainCore.Parsers;

namespace MainCore.Test.Parsers
{
    public class BuildingLayoutParserTest(PlaywrightFixture fixture) : ParserTestBase(fixture)
    {
        [Theory]
        [InlineData("Hero's Mansion")]
        [InlineData("Hero&#39;s Mansion")]
        [InlineData("Hero&rsquo;s mansion")]
        [InlineData("HERO'S&nbsp;MANSION")]
        public async Task ParsesHerosMansionFromConstructionQueue(string displayName)
        {
            await using var context = await CreateContextAsync();
            var page = await context.NewPageAsync();
            await page.SetContentAsync($"""
                <div class="buildingList"><ul><li>
                    <div class="name">{displayName}<span class="lvl">Level 10</span></div>
                    <span class="timer" value="120">0:02:00</span>
                </li></ul></div>
                """);

            var before = DateTime.Now;
            var queue = await BuildingLayoutParser.GetQueueBuilding(page);
            var after = DateTime.Now;

            queue.Count.ShouldBe(1);
            var building = queue[0];
            building.Type.ShouldBe(nameof(BuildingEnums.HerosMansion));
            building.Level.ShouldBe(10);
            building.Location.ShouldBe(-1);
            building.CompleteTime.ShouldBeInRange(before.AddSeconds(120), after.AddSeconds(120));

            var entity = building.ToEntity(new VillageId(1));
            entity.Type.ShouldBe(BuildingEnums.HerosMansion);
            entity.VillageId.ShouldBe(1);
            entity.Level.ShouldBe(10);
        }

        [Theory]
        [InlineData("Hero'sMansion")]
        [InlineData("Hero’s mansion")]
        public void MapsDisplayNameToQueueEntity(string name)
        {
            var dto = new QueueBuildingDto { Type = name, Level = 10 };
            var entity = dto.ToEntity(new VillageId(1));
            entity.Type.ShouldBe(BuildingEnums.HerosMansion);

            var existing = new QueueBuilding();
            dto.To(existing);
            existing.Type.ShouldBe(BuildingEnums.HerosMansion);
        }
    }
}
