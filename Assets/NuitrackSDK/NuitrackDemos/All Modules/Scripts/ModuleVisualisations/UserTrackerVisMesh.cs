using UnityEngine;
using System.Collections;

using NuitrackSDK.Frame;

namespace NuitrackSDK.NuitrackDemos
{
    // Building and processing a mesh (point cloud) using data from a depth sensor
    public class UserTrackerVisMesh : MonoBehaviour
    {
        ulong lastFrameTimestamp = ulong.MaxValue;

        //  List<int[]> triangles;
        //  List<Vector3[]> vertices;
        //  List<Vector2[]> uvs;
        //  List<Vector2[]> uv2s;
        //  List<Vector2[]> uv3s;
        //  List<Vector2[]> uv4s;

        Mesh[] meshes;
        GameObject[] visualizationParts;
        [SerializeField] Material meshMaterial;
        [SerializeField] Color[] userCols;
        Material meshMat;

        Texture2D depthTexture, rgbTexture, segmentationTexture;

        bool active = false;

        bool showBackground = true;

        public void SetActive(bool _active)
        {
            active = _active;
        }

        public void SetShaderProperties(bool showBackground, bool showBorders)
        {
            if (!meshMat)
                meshMat = new Material(meshMaterial);
            this.showBackground = showBackground;
            meshMat.SetInt("_ShowBorders", showBorders ? 1 : 0);
        }

        void Start()
        {
            StartCoroutine(WaitInit());
        }

        IEnumerator WaitInit()
        {
            while (!NuitrackManager.Instance.NuitrackInitialized)
                yield return null;

            InitMeshes(NuitrackManager.sensorsData[0].DepthSensor.GetOutputMode());
        }

        void InitMeshes(nuitrack.OutputMode outputMode)
        {
            meshMat.SetFloat("fX", outputMode.Intrinsics.FX / outputMode.XRes);
            meshMat.SetFloat("fY", outputMode.Intrinsics.FY / outputMode.YRes);
            meshMat.SetFloat("cX", outputMode.Intrinsics.CX / outputMode.XRes);
            meshMat.SetFloat("cY", outputMode.Intrinsics.CY / outputMode.YRes);

            int cols = outputMode.XRes;
            int rows = outputMode.YRes;

            int numMeshes;
            const uint maxVertices = uint.MaxValue;

            numMeshes = (int)((cols * rows) / maxVertices + (((cols * rows) % maxVertices == 0) ? 0 : 1));

            //Debug.Log("Num meshes: " + numMeshes.ToString());

            visualizationParts = new GameObject[numMeshes];
            meshes = new Mesh[numMeshes];

            for (int i = 0; i < numMeshes; i++)
            {
                //Debug.Log("Mesh #" + i.ToString());
                int xLow = (i * cols) / numMeshes;
                int xHigh = (((i + 1) * cols) / numMeshes) + (((i + 1) == numMeshes) ? 0 : 1);
                int numVerts = rows * (xHigh - xLow);
                int numTris = 2 * (rows - 1) * (xHigh - xLow - 1);

                //Debug.Log("xLow = " + xLow.ToString() + "; xHigh = " + xHigh.ToString() + "; verts = " + numVerts.ToString() + "; tris = " + numTris.ToString());

                int[] partTriangles = new int[3 * numTris];
                Vector3[] partVertices = new Vector3[numVerts];
                Vector2[] partUvs = new Vector2[numVerts];

                int index = 0;
                int trisIndex = 0;

                for (int y = 0; y < rows; y++)
                {
                    for (int x = xLow; x < xHigh; x++, index++)
                    {
                        Vector2 depthTextureUV = new Vector2(((float)x + 0.5f) / cols, ((float)y + 0.5f) / rows);
                        partVertices[index] = Vector3.zero;
                        partUvs[index] = depthTextureUV;

                        if ((x < (xHigh - 1)) && (y < (rows - 1)))
                        {
                            partTriangles[trisIndex + 0] = index;
                            partTriangles[trisIndex + 1] = index + (xHigh - xLow);
                            partTriangles[trisIndex + 2] = index + (xHigh - xLow) + 1;

                            partTriangles[trisIndex + 3] = index;
                            partTriangles[trisIndex + 4] = index + (xHigh - xLow) + 1;
                            partTriangles[trisIndex + 5] = index + 1;

                            trisIndex += 6;
                        }
                    }
                }

                meshes[i] = new Mesh();
                meshes[i].indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                meshes[i].vertices = partVertices;
                meshes[i].uv = partUvs;
                meshes[i].triangles = partTriangles;

                Bounds meshBounds = new Bounds(500f * new Vector3(0f, 0f, 1f), 2000f * Vector3.one);
                meshes[i].bounds = meshBounds;

                visualizationParts[i] = new GameObject();
                visualizationParts[i].name = "Visualization_" + i.ToString();
                visualizationParts[i].transform.position = Vector3.zero;
                visualizationParts[i].transform.rotation = Quaternion.identity;
                visualizationParts[i].AddComponent<MeshFilter>();
                visualizationParts[i].GetComponent<MeshFilter>().mesh = meshes[i];
                visualizationParts[i].AddComponent<MeshRenderer>();
                visualizationParts[i].GetComponent<Renderer>().sharedMaterial = meshMat;

            }
        }

        void Update()
        {
            if (!NuitrackManager.Instance.NuitrackInitialized)
                return;

            if (NuitrackManager.sensorsData[0].DepthFrame != null && active)
            {
                var depthFrame = NuitrackManager.sensorsData[0].DepthFrame;
                var colorFrame = NuitrackManager.sensorsData[0].ColorFrame;
                var userFrame  = NuitrackManager.sensorsData[0].UserFrame;

                if (lastFrameTimestamp != depthFrame.Timestamp)
                {
                    ProcessFrame(depthFrame, colorFrame, userFrame);
                    lastFrameTimestamp = depthFrame.Timestamp;
                }
            }
            else
            {
                HideVisualization();
            }
        }

        void HideVisualization()
        {
            if (visualizationParts == null) return;

            foreach (var visPart in visualizationParts)
                if (visPart.activeSelf)
                    visPart.SetActive(false);
        }

        RenderTexture rgbRenderTexture;
        TextureCache  textureCache = new();

        TextureCache depthCache = new();
        Gradient depthGradient = new()
        {
            colorKeys = new GradientColorKey[]
            {
                new GradientColorKey(Color.white, 1),
                new GradientColorKey(Color.black, 0)
            }
        };

        void ProcessFrame(nuitrack.DepthFrame depthFrame, nuitrack.ColorFrame colorFrame, nuitrack.UserFrame userFrame)
        {
            foreach (var visPart in visualizationParts)
                if (!visPart.activeSelf)
                    visPart.SetActive(true);

            rgbTexture = colorFrame == null ? depthFrame.ToTexture2D() : colorFrame.ToTexture2D();

            depthTexture = depthFrame.ToTexture2D(depthGradient, depthCache);
            segmentationTexture = userFrame?.ToTexture2D(userCols, textureCache);

            if (!showBackground && segmentationTexture != null)
            {
                FrameUtils.TextureUtils.Cut(rgbTexture, segmentationTexture, ref rgbRenderTexture);
                meshMat.SetTexture("_RGBTex", rgbRenderTexture);
            }
            else
                meshMat.SetTexture("_RGBTex", rgbTexture);

            meshMat.SetTexture("_DepthTex", depthTexture);
            meshMat.SetTexture("_SegmentationTex", segmentationTexture);
        }

        void OnDestroy()
        {
            if (rgbRenderTexture != null)
                Destroy(rgbRenderTexture);

            textureCache.Dispose();
            textureCache = null;
        }
    }
}
