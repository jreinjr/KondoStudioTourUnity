using UnityEngine;

namespace NuitrackSDK.NuitrackDemos
{
    enum Orientation
    {
        top,
        side,
    }

    public class FovVisualSetter : MonoBehaviour
    {
        [SerializeField] RectTransform root;
        [SerializeField] RectTransform firstLine;
        [SerializeField] RectTransform secondLine;
        [SerializeField] RectTransform parentCanvas;
        [SerializeField] Camera targetCamera;
        [SerializeField] Orientation side;

        void Start()
        {
            var mode = NuitrackManager.sensorsData[0].DepthSensor.GetOutputMode();
            if (side == Orientation.side)
            {
                float fov = 2 * Mathf.Atan(Mathf.Tan(mode.HFOV / 2) * (float)mode.YRes / (float)mode.XRes);
                firstLine.localEulerAngles = new Vector3(0, 0, fov * Mathf.Rad2Deg / 2);
                secondLine.localEulerAngles = new Vector3(0, 0, -fov * Mathf.Rad2Deg / 2);
            }
            else
            {
                firstLine.localEulerAngles = new Vector3(0, 0, mode.HFOV * Mathf.Rad2Deg / 2 - 90);
                secondLine.localEulerAngles = new Vector3(0, 0, -mode.HFOV * Mathf.Rad2Deg / 2 - 90);
            }
        }

        void Update()
        {
            root.offsetMax = side == Orientation.side
                ? new Vector2(parentCanvas.rect.height, 0)
                : new Vector2(0, -parentCanvas.rect.height);
        }
    }
}