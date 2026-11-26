using RebuildSharedData.Enum;
using RoBotClient.Bot.Behavior;

namespace RoBotClient.Tests;

public class SkillPolicyTests
{
    [Fact]
    public void Envenom_OnAPoisonedTarget_LosesToADoubleAttackSwing()
    {
        // hp 90, one hit 40: Envenom L7 (54) on a poisoned mob saves a swing on rounding when a swing is one hit...
        Assert.True(SkillPolicy.ChooseLevel(CharacterSkill.Envenom, 7, true, null, 90, true, 40, 100, true) > 0);
        // ...but not when Double Attack makes the swing it replaces worth 60.
        Assert.Equal(0, SkillPolicy.ChooseLevel(CharacterSkill.Envenom, 7, true, null, 90, true, 60, 100, true, hitDmg: 40));
    }
}
