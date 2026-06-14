using System.Collections.Generic;
using Assets.Scripts.Network;
using Assets.Scripts.PlayerControl;
using RebuildSharedData.Enum.EntityStats;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Assets.Scripts.UI.Stats
{
    public class StatsWindow : WindowBase
    {
        public List<TextMeshProUGUI> BaseStatText;
        public List<TextMeshProUGUI> AddStatText;
        public List<TextMeshProUGUI> StatPointCostText;
        public List<TextMeshProUGUI> AttributeText;

        public List<Button> IncreaseStatButtons;
        public List<Button> DecreaseStatButtons;

        public Button ResetButton;
        public Button ApplyButton;

        private const float AccordionHeight = 118f;

        private Button accordionToggleButton;
        private Button existingInfoButton;
        private RectTransform accordionPanel;
        private TextMeshProUGUI accordionToggleText;
        private Vector2 collapsedWindowSize;
        private bool capturedCollapsedWindowSize;
        private bool normalizedBottomButtonParents;
        private bool isAccordionExpanded;

        //public int[] MinValues;
        private readonly int[] adjustValue = new int[6];
        private int statPointsRequired;

        private static readonly int[] CumulativeStatPointCost = new[]
        {
              2,   4,   6,   8,  10,  12,  14,  16,  18,  20,  22,  25,  28,  31,  34,  37,  40,  43,  46,  49,
             52,  56,  60,  64,  68,  72,  76,  80,  84,  88,  92,  97, 102, 107, 112, 117, 122, 127, 132, 137,
            142, 148, 154, 160, 166, 172, 178, 184, 190, 196, 202, 209, 216, 223, 230, 237, 244, 251, 258, 265,
            272, 280, 288, 296, 304, 312, 320, 328, 336, 344, 352, 361, 370, 379, 388, 397, 406, 415, 424, 433,
            442, 452, 462, 472, 482, 492, 502, 512, 522, 532, 542, 553, 564, 575, 586, 597, 608, 619, 630
        };

        public void ResetStatChanges()
        {
            for (var i = 0; i < 6; i++)
                adjustValue[i] = 0;
            statPointsRequired = 0;
            UpdateCharacterStats();
        }

        public void SaveChanges()
        {
            if (statPointsRequired == 0)
                return;

            NetworkManager.Instance.SendApplyStatPoints(adjustValue);
        }

        public void AddStat(int stat) => ChangeStatValue(stat, 1);
        public void SubStat(int stat) => ChangeStatValue(stat, -1);

        private void ChangeStatValue(int stat, int change)
        {
            var count = (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) ? 10 : 1;
            var state = PlayerState.Instance;
            var curStatPoints = state.GetData(PlayerStat.StatPoints);
            var existing = PlayerState.Instance.GetData(PlayerStat.Str + stat);

            for (var j = 0; j < count; j++)
            {
                if (existing + adjustValue[stat] + change < existing)
                    break;
                if (existing + adjustValue[stat] + change > 99 || existing + adjustValue[stat] + change < 1)
                    break;

                adjustValue[stat] += change;

                var totalChange = 0;
                for (var i = 0; i < 6; i++)
                {
                    var curStatCost = CumulativeStatPointCost[Mathf.Clamp(state.GetData(PlayerStat.Str + i), 1, 99) - 1];
                    var newStatCost = CumulativeStatPointCost[Mathf.Clamp(state.GetData(PlayerStat.Str + i) + adjustValue[i], 1, 99) - 1];
                    totalChange += newStatCost - curStatCost;
                }

                if (totalChange < 0 || totalChange > curStatPoints)
                {
                    adjustValue[stat] -= change;
                    break;
                }

                statPointsRequired = totalChange;
            }

            UpdateCharacterStats();

            // UpdateStat(stat, existing, PlayerState.Instance.GetStat(CharacterStat.AddStr + stat), adjustValue[stat]);
            //
            // var changeSum = 0;
            // for (var i = 0; i < 6; i++)
            //     changeSum += adjustValue[i] != 0 ? 1 : 0;
            //
            // ResetButton.interactable = changeSum != 0;
            // ApplyButton.interactable = changeSum != 0;
            //
            // if(statPointsRequired == 0)
            //     AttributeText[8].text = $"{state.GetData(PlayerStat.StatPoints)}";
            // else
            //     AttributeText[8].text = $"<color=blue>{state.GetData(PlayerStat.StatPoints)-statPointsRequired}";
        }

        private void UpdateStat(int index, int stat, int bonus, int diff = 0)
        {
            if (diff == 0)
                BaseStatText[index].text = stat.ToString();
            if (diff < 0)
                BaseStatText[index].text = $"<color=red>{stat + diff}</color>";
            if (diff > 0)
                BaseStatText[index].text = $"<color=blue>{stat + diff}</color>";
            AddStatText[index].text = bonus switch
            {
                > 0 => $"+{bonus}",
                < 0 => $"{bonus}",
                _ => ""
            };
            var cost = 2 + (stat + diff - 1) / 10;
            if (stat >= 99)
                StatPointCostText[index].text = "-";
            else
                StatPointCostText[index].text = cost.ToString();

            DecreaseStatButtons[index].interactable = diff > 0;
            IncreaseStatButtons[index].interactable =
                stat + diff < 99 && statPointsRequired + cost <= PlayerState.Instance.GetData(PlayerStat.StatPoints);
        }

        public void UpdateCharacterStats()
        {
            EnsureAccordionUi();

            var state = PlayerState.Instance;

            UpdateStat(0, state.GetData(PlayerStat.Str), state.GetStat(CharacterStat.AddStr), adjustValue[0]);
            UpdateStat(1, state.GetData(PlayerStat.Agi), state.GetStat(CharacterStat.AddAgi), adjustValue[1]);
            UpdateStat(2, state.GetData(PlayerStat.Vit), state.GetStat(CharacterStat.AddVit), adjustValue[2]);
            UpdateStat(3, state.GetData(PlayerStat.Int), state.GetStat(CharacterStat.AddInt), adjustValue[3]);
            UpdateStat(4, state.GetData(PlayerStat.Dex), state.GetStat(CharacterStat.AddDex), adjustValue[4]);
            UpdateStat(5, state.GetData(PlayerStat.Luk), state.GetStat(CharacterStat.AddLuk), adjustValue[5]);

            var totalVit = state.GetData(PlayerStat.Vit) + state.GetStat(CharacterStat.AddVit);
            var totalAgi = state.GetData(PlayerStat.Agi) + state.GetStat(CharacterStat.AddAgi);
            var totalInt = state.GetData(PlayerStat.Int) + state.GetStat(CharacterStat.AddInt);
            var totalDex = state.GetData(PlayerStat.Dex) + state.GetStat(CharacterStat.AddDex);
            var totalLuk = state.GetData(PlayerStat.Luk) + state.GetStat(CharacterStat.AddLuk);

            var softDef = totalVit * (100 + state.GetStat(CharacterStat.AddSoftDefPercent)) / 100;

            var crit = 1 + (totalLuk / 3) + state.GetStat(CharacterStat.AddCrit);
            if (state.WeaponClass == 16) //katar
                crit *= 2;

            var changeSum = 0;
            for (var i = 0; i < 6; i++)
                changeSum += adjustValue[i] != 0 ? 1 : 0;

            ResetButton.interactable = changeSum != 0;
            ApplyButton.interactable = changeSum != 0;

            AttributeText[0].text = $"{state.GetStat(CharacterStat.Attack)} ~ {state.GetStat(CharacterStat.Attack2)}";
            AttributeText[1].text = $"{state.GetStat(CharacterStat.MagicAtkMin)} ~ {state.GetStat(CharacterStat.MagicAtkMax)}";
            AttributeText[2].text = $"{totalDex + state.Level + state.GetStat(CharacterStat.AddHit)}";
            AttributeText[3].text = $"{crit}";
            AttributeText[4].text = $"{state.GetStat(CharacterStat.Def)} + {softDef}";
            AttributeText[5].text = $"{state.GetStat(CharacterStat.MDef)} + {totalInt}";
            AttributeText[6].text = $"{totalAgi + state.Level + state.GetStat(CharacterStat.AddFlee)} + {state.GetStat(CharacterStat.PerfectDodge)}";
            AttributeText[7].text = $"{(1 / state.AttackSpeed):F2}/sec";
            if (statPointsRequired == 0)
                AttributeText[8].text = $"{state.GetData(PlayerStat.StatPoints)}";
            else
                AttributeText[8].text = $"<color=blue>{state.GetData(PlayerStat.StatPoints) - statPointsRequired}";
        }
        private void EnsureAccordionUi()
        {
            if (accordionPanel != null)
                return;

            var windowRect = transform as RectTransform;
            if (windowRect == null)
                return;

            collapsedWindowSize = windowRect.sizeDelta;
            capturedCollapsedWindowSize = true;

            existingInfoButton = FindExistingInfoButton();
            NormalizeExistingBottomButtons(windowRect);

            accordionPanel = CreateAccordionPanel(windowRect);
            accordionToggleButton = CreateAccordionToggle(windowRect);

            SetAccordionExpanded(false);
        }

        private Button FindExistingInfoButton()
        {
            var buttons = GetComponentsInChildren<Button>(true);
            foreach (var button in buttons)
            {
                var buttonText = button.GetComponentInChildren<TextMeshProUGUI>(true);
                if (buttonText == null)
                    continue;

                if (buttonText.text.Trim() == "Info")
                    return button;
            }

            return null;
        }

        private void NormalizeExistingBottomButtons(RectTransform windowRect)
        {
            if (normalizedBottomButtonParents)
                return;

            ReparentButtonToWindowRootPreservingPosition(ApplyButton, windowRect);
            ReparentButtonToWindowRootPreservingPosition(ResetButton, windowRect);
            ReparentButtonToWindowRootPreservingPosition(existingInfoButton, windowRect);

            normalizedBottomButtonParents = true;
        }

        private void ReparentButtonToWindowRootPreservingPosition(Button button, RectTransform windowRect)
        {
            if (button == null || windowRect == null)
                return;

            if (button.transform is not RectTransform buttonRect)
                return;

            if (buttonRect.parent == windowRect)
                return;

            var buttonSize = buttonRect.rect.size;
            if (buttonSize.x <= 0f || buttonSize.y <= 0f)
                buttonSize = buttonRect.sizeDelta;

            var worldCorners = new Vector3[4];
            buttonRect.GetWorldCorners(worldCorners);
            var bottomLeftInWindow = (Vector2)windowRect.InverseTransformPoint(worldCorners[0]);

            var windowRectData = windowRect.rect;
            var bottomLeftAnchorPosition = new Vector2(windowRectData.xMin, windowRectData.yMin);
            var anchoredBottomLeft = bottomLeftInWindow - bottomLeftAnchorPosition;

            buttonRect.SetParent(windowRect, false);
            buttonRect.anchorMin = Vector2.zero;
            buttonRect.anchorMax = Vector2.zero;
            buttonRect.pivot = Vector2.zero;
            buttonRect.sizeDelta = buttonSize;
            buttonRect.anchoredPosition = anchoredBottomLeft;
            buttonRect.localScale = Vector3.one;
        }

        private TextMeshProUGUI GetButtonTextStyleSource()
        {
            if (existingInfoButton != null)
            {
                var infoText = existingInfoButton.GetComponentInChildren<TextMeshProUGUI>(true);
                if (infoText != null)
                    return infoText;
            }

            if (ApplyButton != null)
            {
                var applyText = ApplyButton.GetComponentInChildren<TextMeshProUGUI>(true);
                if (applyText != null)
                    return applyText;
            }

            if (ResetButton != null)
            {
                var resetText = ResetButton.GetComponentInChildren<TextMeshProUGUI>(true);
                if (resetText != null)
                    return resetText;
            }

            return null;
        }

        private TextMeshProUGUI GetBodyTextStyleSource()
        {
            if (AttributeText != null && AttributeText.Count > 0 && AttributeText[0] != null)
                return AttributeText[0];

            if (BaseStatText != null && BaseStatText.Count > 0 && BaseStatText[0] != null)
                return BaseStatText[0];

            return GetButtonTextStyleSource();
        }

        private Button CreateAccordionToggle(RectTransform parent)
        {
            var toggleObject = new GameObject("StatsAccordionToggle", typeof(RectTransform), typeof(Image), typeof(Button));
            toggleObject.transform.SetParent(parent, false);

            var toggleRect = toggleObject.GetComponent<RectTransform>();

            if (existingInfoButton != null)
            {
                var infoRect = existingInfoButton.transform as RectTransform;
                if (infoRect != null)
                {
                    toggleRect.anchorMin = new Vector2(0f, 0f);
                    toggleRect.anchorMax = new Vector2(0f, 0f);
                    toggleRect.pivot = new Vector2(0f, 1f);
                    toggleRect.sizeDelta = infoRect.sizeDelta;
                    toggleRect.anchoredPosition = new Vector2(0f, 3f);
                }

                var infoImage = existingInfoButton.GetComponent<Image>();
                var toggleImage = toggleObject.GetComponent<Image>();
                CopyImageStyle(infoImage, toggleImage);

                var button = toggleObject.GetComponent<Button>();
                CopyButtonStyle(existingInfoButton, button, toggleImage);
                button.onClick.AddListener(ToggleAccordion);

                accordionToggleText = CreateClonedText(toggleObject.transform, GetButtonTextStyleSource(), "Text");
                var textRect = accordionToggleText.rectTransform;
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = Vector2.zero;
                textRect.offsetMax = Vector2.zero;
                textRect.localScale = Vector3.one;

                accordionToggleText.text = "More ▼";
                accordionToggleText.alignment = TextAlignmentOptions.Center;
                accordionToggleText.enableWordWrapping = false;
                accordionToggleText.overflowMode = TextOverflowModes.Overflow;
                accordionToggleText.raycastTarget = false;

                toggleObject.transform.SetSiblingIndex(existingInfoButton.transform.GetSiblingIndex());

                return button;
            }

            toggleRect.anchorMin = new Vector2(0f, 0f);
            toggleRect.anchorMax = new Vector2(0f, 0f);
            toggleRect.pivot = new Vector2(0f, 1f);
            toggleRect.anchoredPosition = new Vector2(0f, 3f);
            toggleRect.sizeDelta = new Vector2(70f, 30f);

            var fallbackImage = toggleObject.GetComponent<Image>();
            fallbackImage.color = new Color(0.78f, 0.78f, 0.78f, 1f);

            var fallbackButton = toggleObject.GetComponent<Button>();
            fallbackButton.targetGraphic = fallbackImage;
            fallbackButton.onClick.AddListener(ToggleAccordion);

            accordionToggleText = CreateClonedText(toggleObject.transform, GetButtonTextStyleSource(), "Text");
            var fallbackTextRect = accordionToggleText.rectTransform;
            fallbackTextRect.anchorMin = Vector2.zero;
            fallbackTextRect.anchorMax = Vector2.one;
            fallbackTextRect.offsetMin = Vector2.zero;
            fallbackTextRect.offsetMax = Vector2.zero;
            fallbackTextRect.localScale = Vector3.one;

            accordionToggleText.text = "More ▼";
            accordionToggleText.alignment = TextAlignmentOptions.Center;
            accordionToggleText.enableWordWrapping = false;
            accordionToggleText.overflowMode = TextOverflowModes.Overflow;

            return fallbackButton;
        }

        private void CopyImageStyle(Image source, Image target)
        {
            if (source == null || target == null)
                return;

            target.sprite = source.sprite;
            target.type = source.type;
            target.preserveAspect = source.preserveAspect;
            target.fillCenter = source.fillCenter;
            target.fillMethod = source.fillMethod;
            target.fillOrigin = source.fillOrigin;
            target.fillAmount = source.fillAmount;
            target.fillClockwise = source.fillClockwise;
            target.material = source.material;
            target.color = source.color;
            target.raycastTarget = source.raycastTarget;
            target.maskable = source.maskable;
            target.pixelsPerUnitMultiplier = source.pixelsPerUnitMultiplier;
        }

        private void CopyButtonStyle(Button source, Button target, Graphic targetGraphic)
        {
            if (source == null || target == null)
                return;

            target.interactable = source.interactable;
            target.transition = source.transition;
            target.colors = source.colors;
            target.spriteState = source.spriteState;
            target.animationTriggers = source.animationTriggers;
            target.navigation = source.navigation;
            target.targetGraphic = targetGraphic;
        }

        private RectTransform CreateAccordionPanel(RectTransform parent)
        {
            var panelObject = new GameObject("StatsAccordionPanel", typeof(RectTransform));
            panelObject.transform.SetParent(parent, false);

            var panelRect = panelObject.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 0f);
            panelRect.anchorMax = new Vector2(1f, 0f);
            panelRect.pivot = new Vector2(0.5f, 0f);
            panelRect.anchoredPosition = new Vector2(0f, 34f);
            panelRect.sizeDelta = new Vector2(-18f, AccordionHeight - 42f);

            var separatorObject = new GameObject("AccordionSeparator", typeof(RectTransform), typeof(Image));
            separatorObject.transform.SetParent(panelObject.transform, false);

            var separatorRect = separatorObject.GetComponent<RectTransform>();
            separatorRect.anchorMin = new Vector2(0f, 1f);
            separatorRect.anchorMax = new Vector2(1f, 1f);
            separatorRect.pivot = new Vector2(0.5f, 1f);
            separatorRect.anchoredPosition = Vector2.zero;
            separatorRect.sizeDelta = new Vector2(-12f, 1f);

            var separatorImage = separatorObject.GetComponent<Image>();
            separatorImage.color = new Color(0.72f, 0.72f, 0.72f, 1f);

            var titleText = CreateClonedText(panelObject.transform, GetBodyTextStyleSource(), "AccordionTitle");
            var titleRect = titleText.rectTransform;
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0f, 1f);
            titleRect.anchoredPosition = new Vector2(10f, -8f);
            titleRect.sizeDelta = new Vector2(-20f, 22f);
            titleRect.localScale = Vector3.one;

            titleText.text = "Character Notes";
            titleText.fontSize = 18f;
            titleText.fontStyle = FontStyles.Bold;
            titleText.alignment = TextAlignmentOptions.Left;
            titleText.enableWordWrapping = false;
            titleText.overflowMode = TextOverflowModes.Overflow;

            var bodyText = CreateClonedText(panelObject.transform, GetBodyTextStyleSource(), "AccordionBody");
            var bodyRect = bodyText.rectTransform;
            bodyRect.anchorMin = Vector2.zero;
            bodyRect.anchorMax = Vector2.one;
            bodyRect.offsetMin = new Vector2(10f, 8f);
            bodyRect.offsetMax = new Vector2(-10f, -34f);
            bodyRect.localScale = Vector3.one;

            bodyText.text = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer vitae sem sed lorem gravida luctus. Donec feugiat justo at porta facilisis.";
            bodyText.fontSize = 18f;
            bodyText.fontStyle = FontStyles.Normal;
            bodyText.alignment = TextAlignmentOptions.TopLeft;
            bodyText.enableWordWrapping = true;
            bodyText.overflowMode = TextOverflowModes.Truncate;

            return panelRect;
        }

        private TextMeshProUGUI CreateClonedText(Transform parent, TextMeshProUGUI styleSource, string objectName)
        {
            TextMeshProUGUI text;

            if (styleSource != null)
            {
                text = Instantiate(styleSource, parent, false);
                text.name = objectName;
            }
            else
            {
                var textObject = new GameObject(objectName, typeof(RectTransform), typeof(TextMeshProUGUI));
                textObject.transform.SetParent(parent, false);
                text = textObject.GetComponent<TextMeshProUGUI>();
            }

            text.rectTransform.localScale = Vector3.one;
            text.color = Color.black;
            text.enableAutoSizing = false;
            text.raycastTarget = false;
            return text;
        }

        private void ToggleAccordion()
        {
            SetAccordionExpanded(!isAccordionExpanded);
        }

        private void SetAccordionExpanded(bool expanded)
        {
            isAccordionExpanded = expanded;

            if (accordionPanel != null)
                accordionPanel.gameObject.SetActive(expanded);

            if (accordionToggleText != null)
                accordionToggleText.text = expanded ? "Less ▲" : "More ▼";

            var windowRect = transform as RectTransform;
            if (windowRect == null || !capturedCollapsedWindowSize)
                return;

            windowRect.sizeDelta = expanded
                ? new Vector2(collapsedWindowSize.x, collapsedWindowSize.y + AccordionHeight)
                : collapsedWindowSize;
        }

    }
}