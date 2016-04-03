using UnityEngine;

[RequireComponent(typeof(Camera))]
[ExecuteInEditMode]
public class ScreenSpaceLocalReflection : MonoBehaviour 
{
    enum Quality { High, Middle, Low }

    [SerializeField] Shader shader;

    [HeaderAttribute("Quality")]
    [SerializeField] Quality quality = Quality.Middle;
    [Range(0f, 1f)][SerializeField] float resolution = 0.5f;
    int width  { get { return (int)(GetComponent<Camera>().pixelWidth  * resolution); } }
    int height { get { return (int)(GetComponent<Camera>().pixelHeight * resolution); } }

    [HeaderAttribute("Raytrace")]
    [SerializeField] float raytraceMaxLength = 2f;
    [SerializeField] float raytraceMaxThickness = 0.2f;

    [HeaderAttribute("Blur")]
    [SerializeField] Vector2 blurOffset = new Vector2(1f, 1f);
    [Range(0, 10)][SerializeField] uint blurNum = 3;

    [HeaderAttribute("Reflection")]
    [Range(0f, 5f)][SerializeField] float reflectionEnhancer = 1f;

    [HeaderAttribute("Smoothness")]
    [SerializeField] bool useSmoothness = false;
    [Range(3, 10)][SerializeField] int maxSmoothness = 5;

    [HeaderAttribute("Accumulation")]
    [Range(0f, 1f)][SerializeField] float accumulationBlendRatio = 0.1f;


    Material material_;
    Mesh screenQuad_;
    RenderTexture[] accumulationTextures_ = new RenderTexture[2];
    Matrix4x4 preViewProj_ = Matrix4x4.identity;

    Mesh CreateQuad()
    {
        Mesh mesh = new Mesh();
        mesh.name = "Quad";
        mesh.vertices = new Vector3[4] {
            new Vector3( 1f, 1f, 0f),
            new Vector3(-1f, 1f, 0f),
            new Vector3(-1f,-1f, 0f),
            new Vector3( 1f,-1f, 0f),
        };
        mesh.triangles = new int[6] {
            0, 1, 2,
            2, 3, 0
        };
        return mesh;
    }

    void ReleaseTexture(ref RenderTexture texture)
    {
        if (texture != null) {
            texture.Release();
            texture = null;
        }
    }

    void UpdateTexture(ref RenderTexture texture, RenderTextureFormat format)
    {
        if (texture != null && (texture.width  != width || texture.height != height)) {
            ReleaseTexture(ref texture);
        }

        if (texture == null || !texture.IsCreated()) {
            texture = new RenderTexture(width, height, 0, format);
            texture.filterMode = FilterMode.Bilinear;
            texture.useMipMap = false;
            texture.generateMips = false;
            texture.enableRandomWrite = true;
            texture.Create();
            Graphics.SetRenderTarget(texture);
            GL.Clear(false, true, new Color(0, 0, 0, 0));
        }
    }

    void ReleaseTextures()
    {
        for (int i = 0; i < 2; ++i) {
            ReleaseTexture(ref accumulationTextures_[i]);
        }
    }

    void UpdateAccumulationTexture()
    {

        for (int i = 0; i < 2; ++i) {
            UpdateTexture(ref accumulationTextures_[i], RenderTextureFormat.ARGB32);
        }
    }

    [ImageEffectOpaque]
    void OnRenderImage(RenderTexture src, RenderTexture dst)
    {
        if (shader == null) return;

        if (material_ == null) {
            material_ = new Material(shader);
        }

        if (screenQuad_ == null) {
            screenQuad_ = CreateQuad();
        }

        UpdateAccumulationTexture();

        material_.SetVector("_Params1", new Vector4(
            raytraceMaxLength,
            raytraceMaxThickness,
            reflectionEnhancer,
            accumulationBlendRatio));

        var camera = GetComponent<Camera>();
        var view = camera.worldToCameraMatrix;
        var proj = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false);
        var viewProj = proj * view;
        material_.SetMatrix("_ViewProj", viewProj);
        material_.SetMatrix("_InvViewProj", viewProj.inverse);

        var reflectionTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
        reflectionTexture.filterMode = FilterMode.Bilinear;
        var xBlurredTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
        xBlurredTexture.filterMode = FilterMode.Bilinear;
        var yBlurredTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
        yBlurredTexture.filterMode = FilterMode.Bilinear;

        switch (quality) {
            case Quality.High:
                material_.EnableKeyword("QUALITY_HIGH");
                material_.DisableKeyword("QUALITY_MIDDLE");
                material_.DisableKeyword("QUALITY_LOW");
                break;
            case Quality.Middle:
                material_.DisableKeyword("QUALITY_HIGH");
                material_.EnableKeyword("QUALITY_MIDDLE");
                material_.DisableKeyword("QUALITY_LOW");
                break;
            case Quality.Low:
                material_.DisableKeyword("QUALITY_HIGH");
                material_.DisableKeyword("QUALITY_MIDDLE");
                material_.EnableKeyword("QUALITY_LOW");
                break;
        }

        {
            Graphics.Blit(src, reflectionTexture, material_, 0);
            material_.SetTexture("_ReflectionTexture", reflectionTexture);
        }

        if (blurNum > 0) {
            Graphics.SetRenderTarget(xBlurredTexture);
            material_.SetVector("_BlurParams", new Vector4(blurOffset.x, 0f, blurNum, 0));
            material_.SetPass(1);
            Graphics.DrawMeshNow(screenQuad_, Matrix4x4.identity);
            material_.SetTexture("_ReflectionTexture", xBlurredTexture);

            Graphics.SetRenderTarget(yBlurredTexture);
            material_.SetVector("_BlurParams", new Vector4(0f, blurOffset.y, blurNum, 0));
            material_.SetPass(1);
            Graphics.DrawMeshNow(screenQuad_, Matrix4x4.identity);
            material_.SetTexture("_ReflectionTexture", yBlurredTexture);
        }

        {
            if (preViewProj_ == Matrix4x4.identity) {
                preViewProj_ = viewProj;
            }

            Graphics.SetRenderTarget(accumulationTextures_[0]);
            material_.SetMatrix("_PreViewProj", preViewProj_);
            material_.SetTexture("_PreAccumulationTexture", accumulationTextures_[1]);
            material_.SetPass(2);
            Graphics.DrawMeshNow(screenQuad_, Matrix4x4.identity);
            material_.SetTexture("_AccumulationTexture", accumulationTextures_[0]);

            var tmp = accumulationTextures_[1];
            accumulationTextures_[1] = accumulationTextures_[0];
            accumulationTextures_[0] = tmp;

            preViewProj_ = viewProj;
        }

        if (useSmoothness) {
            material_.EnableKeyword("USE_SMOOTHNESS");

            material_.SetTexture("_ReflectionTexture", accumulationTextures_[1]);

            Graphics.SetRenderTarget(xBlurredTexture);
            material_.SetVector("_BlurParams", new Vector4(blurOffset.x, 0f, maxSmoothness, 0));
            material_.SetPass(1);
            Graphics.DrawMeshNow(screenQuad_, Matrix4x4.identity);

            Graphics.SetRenderTarget(yBlurredTexture);
            material_.SetTexture("_ReflectionTexture", xBlurredTexture);
            material_.SetVector("_BlurParams", new Vector4(0f, blurOffset.y, maxSmoothness, 0));
            material_.SetPass(1);
            Graphics.DrawMeshNow(screenQuad_, Matrix4x4.identity);

            material_.SetTexture("_SmoothnessTexture", yBlurredTexture);
        } else {
            material_.DisableKeyword("USE_SMOOTHNESS");
        }

        {
            Graphics.SetRenderTarget(dst);
            Graphics.Blit(src, dst, material_, 3);
        }

        RenderTexture.ReleaseTemporary(reflectionTexture);
        RenderTexture.ReleaseTemporary(xBlurredTexture);
        RenderTexture.ReleaseTemporary(yBlurredTexture);
    }
}