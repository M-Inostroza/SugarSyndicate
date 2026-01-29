using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "SugarSyndicate/Tutorial Steps", fileName = "TutorialSteps")]
public class TutorialStepsAsset : ScriptableObject
{
    public List<OnboardingManager.Step> steps = new List<OnboardingManager.Step>();
}
