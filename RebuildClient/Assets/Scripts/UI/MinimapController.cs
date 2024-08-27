using System.Collections;
using System.Collections.Generic;
using RebuildSharedData.Enum;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.UI;

public class MinimapController : MonoBehaviour
{
    public GameObject ContentContainer;
    public GameObject Viewport;

    public Image MapImage;
    public Material OverworldMaterial;
    public Material DungeonMaterial;
    public Sprite PlayerIcon;
    public Sprite OtherPlayerIcon;
    public Sprite BossIcon;
    public Sprite MvpIcon;
    public Sprite PortalIcon;
    public Slider ZoomSlider;
    
    private GameObject playerMapIconObject;
    private Dictionary<int, MinimapEntityData> mapIcons = new();

    public MapType MapType;

    public float ObjectScaleFactor = 1f;
    public float MinimapPixelsPerTile = 5f;

    public float minScale;
    public float maxScale;

    public float curSize;
    public float lastZoom;

    private float offsetX;
    private float offsetY;
    
    private static MinimapController instance;

    private Coroutine loadCoroutine;

    private Sprite mapSprite;
    public Sprite walkSprite;
    
    private class MinimapEntityData
    {
        public GameObject MapIcon;
        public Vector2Int Position;
        public CharacterDisplayType Type;
    }

   

    public static MinimapController Instance
    {
        get
        {
            if (instance != null)
                return instance;
            instance = FindObjectOfType<MinimapController>();
            return instance;
        }
    }

    public void RemoveAllEntities()
    {
        if (mapIcons == null) return;
        
        foreach(var icon in mapIcons)
            Destroy(icon.Value.MapIcon);
        mapIcons.Clear();
    }

    public void RemoveEntity(int entityId)
    {
        if (mapIcons == null || !mapIcons.Remove(entityId, out var mapIcon))
            return;

        Destroy(mapIcon.MapIcon);
    }

    public void SetEntityPosition(int entityId, CharacterDisplayType type, Vector2Int pos)
    {
        if (!mapIcons.TryGetValue(entityId, out var iconData))
        {
            iconData = new MinimapEntityData() { MapIcon = null, Position = pos, Type = type };
            mapIcons.Add(entityId, iconData);
        }

        if (!gameObject.activeInHierarchy || MapImage == null || mapSprite == null)
            return;

        var scale = 0.3f;
        if (type == CharacterDisplayType.Boss || type == CharacterDisplayType.Mvp)
            scale = 0.4f;
        if (type == CharacterDisplayType.Portal)
            scale = 0.08f;

        GameObject mapIcon = iconData.MapIcon;

        if (mapIcon == null)
        {
            mapIcon = new GameObject("PlayerIcon");
            mapIcon.transform.SetParent(MapImage.transform, false);


            var img = mapIcon.AddComponent<Image>();
            switch (type)
            {
                case CharacterDisplayType.Player: img.sprite = OtherPlayerIcon; break;
                case CharacterDisplayType.Boss: img.sprite = BossIcon; break;
                case CharacterDisplayType.Mvp: img.sprite = MvpIcon; break;
                case CharacterDisplayType.Portal: img.sprite = PortalIcon; break;
                default: 
                    Debug.Log($"Unknown character display type for minimap icon: {type}");
                    img.sprite = OtherPlayerIcon;
                    break;
            }

            iconData.MapIcon = mapIcon;
        }
        
        var r = mapIcon.GetComponent<RectTransform>();

        r.anchorMin = Vector2.zero;
        r.anchorMax = Vector2.zero;

        var w = mapSprite.texture.width;
        var h = mapSprite.texture.height;
        var offset = new Vector3(0.5f, 0.5f, 0);

        r.localPosition = new Vector3(pos.x * MinimapPixelsPerTile / 2f, pos.y * MinimapPixelsPerTile / 2f - h, 0f) + offset;

        var px = (pos.x * MinimapPixelsPerTile / 2f + offsetX) * curSize;
        var py = ((h - pos.y * MinimapPixelsPerTile / 2f) + offsetY) * curSize;

        var scrollx = px - 125f;
        var scrolly = py - 125f;

        var maxScroll = ((Mathf.Max(w, h) * curSize - 250f));

        scrollx = Mathf.Clamp(-scrollx, -maxScroll, 0);
        scrolly = Mathf.Clamp(scrolly, 0, maxScroll);

        ContentContainer.GetComponent<RectTransform>().anchoredPosition = new Vector3(scrollx, scrolly, 0f);

        var s = scale * ObjectScaleFactor * (1 / curSize);
        
        mapIcon.transform.localScale = Vector3.one * s;

        playerMapIconObject?.transform.SetAsLastSibling();
    }

    public void SetPlayerPosition(Vector2Int pos, float angle)
    {
        if (!gameObject.activeInHierarchy || MapImage == null || mapSprite == null)
            return;

        if (playerMapIconObject == null)
        {
            playerMapIconObject = new GameObject("PlayerIcon");
            playerMapIconObject.transform.SetParent(MapImage.transform, false);


            var img = playerMapIconObject.AddComponent<Image>();
            img.sprite = PlayerIcon;

         
            //var w = mapSprite.texture.width;
            //var h = mapSprite.texture.height;
         
        }
        //Debug.Log(pos + " " + new Vector3(pos.x * 10f, pos.y * 10f, 0f));

        var r = playerMapIconObject.GetComponent<RectTransform>();

        r.anchorMin = Vector2.zero;
        r.anchorMax = Vector2.zero;

        var w = mapSprite.texture.width;
        var h = mapSprite.texture.height;
        var offset = new Vector3(0.5f, 0.5f, 0);

        r.localPosition = new Vector3(pos.x * MinimapPixelsPerTile / 2f, pos.y * MinimapPixelsPerTile / 2f - h, 0f) + offset;

        //ScrollRect.horizontalNormalizedPosition = pos.x / (float)w;

        var px = (pos.x * MinimapPixelsPerTile / 2f + offsetX) * curSize;
        var py = ((h - pos.y * MinimapPixelsPerTile / 2f) + offsetY) * curSize;

        var scrollx = px - 125f;
        var scrolly = py - 125f;



        var maxScroll = ((Mathf.Max(w, h) * curSize - 250f));


        scrollx = Mathf.Clamp(-scrollx, -maxScroll, 0);
        scrolly = Mathf.Clamp(scrolly, 0, maxScroll);

        //Debug.Log($"{curSize} {px} {py} {scrollx} {scrolly} {maxScroll}");

        ContentContainer.GetComponent<RectTransform>().anchoredPosition = new Vector3(scrollx, scrolly, 0f);

        //playerMapIconObject.transform.localPosition = new Vector3(pos.x * 10f, pos.y * 10f, 0f);
        playerMapIconObject.transform.localRotation = Quaternion.Euler(0f, 0f, -angle);

        var s = 0.3f * ObjectScaleFactor * (1 / curSize);
        
        playerMapIconObject.transform.localScale = Vector3.one * s;
    }

    public void LoadMinimap(string mapName, MapType type)
    {
        if(loadCoroutine != null)
            StopCoroutine(loadCoroutine);

        gameObject.SetActive(true);
        ContentContainer.SetActive(false);
        //if(mapSprite != null)
        //    Destroy(mapSprite);
        mapSprite = null;
        //if(walkSprite != null)
        //    Destroy(walkSprite);
        walkSprite = null;
        MapType = type;

        loadCoroutine = StartCoroutine(LoadMinimapCoroutine(mapName));
    }

    public void SetZoom(float zoom)
    {
        zoom = Mathf.Clamp(zoom, minScale, maxScale);
        //Debug.Log($"Setting minimap size to {zoom} (in a range of {minScale} to {maxScale})");

        curSize = zoom;

        if (mapSprite == null)
            return;

        UpdateMapMaterial();

        var w = mapSprite.texture.width;
        var h = mapSprite.texture.height;

        MapImage.rectTransform.sizeDelta = new Vector2(w, h);

        var containerRect = ContentContainer.GetComponent<RectTransform>();
        containerRect.sizeDelta = MapImage.rectTransform.sizeDelta;
        containerRect.localScale = new Vector3(curSize, curSize, curSize);

        offsetX = 0f;
        offsetY = 0f;

        if (w != h && (w * curSize < 250 || h * curSize < 250))
        {

            if (w > h)
                offsetY = -(w - h) / 2f;

            else
                offsetX = (h - w) / 2f;

            MapImage.transform.localPosition = new Vector3(offsetX, offsetY, 0);
        }
        else
            MapImage.transform.localPosition = Vector3.zero;

        lastZoom = curSize;
        
        if(mapIcons.Count > 0)
            foreach(var icon in mapIcons)
                SetEntityPosition(icon.Key, icon.Value.Type, icon.Value.Position);
    }

    public void UpdateZoomFromSlider()
    {
        
        SetZoom(LeanTween.easeInQuad(minScale, maxScale, ZoomSlider.value/ZoomSlider.maxValue));
        
        //SetZoom(ZoomSlider.value.Remap(ZoomSlider.minValue, ZoomSlider.maxValue, minScale, maxScale));
    }

    private void UpdateMapMaterial()
    {

        MapImage.sprite = mapSprite;

        if (MapType == MapType.Dungeon)
        {
            MapImage.sprite = walkSprite;
            MapImage.material = DungeonMaterial;
        }
        else
        {
            if (MapType == MapType.Town)
            {
                //dungeon material but with regular map, so no highlighted walk
                MapImage.material = DungeonMaterial;
            }
            else
            {
                MapImage.material = OverworldMaterial;
                OverworldMaterial.SetTexture("_SecondaryTex", walkSprite.texture);
            }

        }
    }

    public IEnumerator LoadMinimapCoroutine(string mapName)
    {
        yield return new WaitForEndOfFrame();

        var loadMap = Addressables.LoadAssetAsync<Sprite>($"Assets/Maps/minimap/{mapName}.png");
        var loadWalk = Addressables.LoadAssetAsync<Sprite>($"Assets/Maps/minimap/{mapName}_walkmask.png");

        yield return loadMap;
        yield return loadWalk;

        if (!loadWalk.IsDone || !loadWalk.IsValid() || !loadMap.IsDone || !loadMap.IsValid())
        {
            Debug.LogWarning("Could not load minimap.");
            yield break; //give up
        }

        //var map = loadMap.Result;

        mapSprite = loadMap.Result;
        walkSprite = loadWalk.Result;

        UpdateMapMaterial();

        minScale = 250f / mapSprite.texture.width;
        
        if (250f / mapSprite.texture.height < minScale)
            minScale = 250f / mapSprite.texture.height;

        maxScale = minScale * 6f;

        maxScale = Mathf.Clamp(maxScale, 2f, 10f);

        if (minScale > maxScale)
            maxScale = minScale;



        //var sprite = Sprite.Create(map, new Rect(0, 0, map.width, map.height), new Vector2(0, 1), 1);

        ContentContainer.transform.localPosition = new Vector3(0, 0, 0f);

        UpdateZoomFromSlider();

        ContentContainer.gameObject.SetActive(true);

        if (mapIcons.Count > 0)
        {
            foreach(var icon in mapIcons)
                SetEntityPosition(icon.Key, icon.Value.Type, icon.Value.Position);
        }
    }

    void Awake()
    {
        instance = this;
        OverworldMaterial = new Material(OverworldMaterial);
        DungeonMaterial = new Material(DungeonMaterial);
    }

    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        if(!Mathf.Approximately(curSize, lastZoom))
            SetZoom(curSize);
    }
}