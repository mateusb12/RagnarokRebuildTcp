using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[ExecuteInEditMode]
public class ScreenshotCamera : MonoBehaviour
{
    public bool TakeScreenshot;
    public int Width = 4096;
    public int Height = 4096;
    public string FileName;

    // Start is called before the first frame update
    void Start()
    {

    }

    // Update is called once per frame
    void Update()
    {
        if(TakeScreenshot)
            TakeScreenshotCoroutine();
        TakeScreenshot = false;
    }

    [ContextMenu("Take Screenshot")]
    public void TakeScreenshotCoroutine()
    {
        //yield return new WaitForEndOfFrame();

        var cam = GetComponent<Camera>();
        var rt = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGB32);
        cam.targetTexture = rt;
        
        cam.Render();

        var prevRT = RenderTexture.active;
        RenderTexture.active = cam.targetTexture;

        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
        tex.Apply();
        
        var bytes = tex.EncodeToJPG();


        if (!Directory.Exists("Assets/Maps/minimap"))
            Directory.CreateDirectory("Assets/Maps/minimap");
        File.WriteAllBytes($@"Assets/Maps/minimap/{FileName}.jpg", bytes);

        cam.targetTexture = null;
        RenderTexture.active = prevRT;
        
        DestroyImmediate(rt);
        DestroyImmediate(tex);
    }

}
