using Assets.Scripts;
using Assets.Scripts.Sprites;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

public class EmoteEntry : MonoBehaviour, IPointerClickHandler
{
    public RoSpriteRendererUI SpriteRenderer;
    public TextMeshProUGUI Text;

    private int emoteId;

    public void SetEmote(int id, int frame, Vector2 pos, float scale, string text)
    {
        if (SpriteRenderer == null)
        {
            Debug.LogError("SpriteRenderer is not assigned!");
            return;
        }
        if (SpriteRenderer.SpriteData == null)
        {
            Debug.LogError("SpriteRenderer.SpriteData is null!");
            return;
        }
        emoteId = id;

        SpriteRenderer.ActionId = id;
        SpriteRenderer.CurrentFrame = frame;
        SpriteRenderer.OffsetPosition = pos;
        Text.text = text;

        SpriteRenderer.gameObject.transform.localScale = Vector3.one * scale;
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        CameraFollower.Instance.Emote(emoteId);
    }

    public void Awake()
    {
        //SpriteRenderer.Rebuild();
        if (SpriteRenderer == null)
        {
            Debug.LogError("Failed to find RoSpriteRendererUI on this GameObject.");
        }
    }
}
