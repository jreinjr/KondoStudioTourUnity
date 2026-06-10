using UnityEngine;

namespace NuitrackSDK.NuitrackDemos
{
    struct ModuleUpdates
    {
        public ulong lastTimeStamp;
        public int updates;
        public int totalUpdates;
    }

    public class FPSCounter : MonoBehaviour
    {
        [SerializeField] bool showNuitrackInfo;
        [SerializeField] float measureTime = 1f;

        float min_fps;
        float avg_fps;
        TextMesh tm;

        float timer = 0f;
        int frames = 0;

        ModuleUpdates userUpdates;
        ModuleUpdates skelUpdates;
        ModuleUpdates handUpdates;

        void Start()
        {
            tm = gameObject.GetComponent<TextMesh>();
        }

        void Update()
        {
            timer += Time.unscaledDeltaTime;
            ++frames;

            if (min_fps == 0)
            {
                min_fps = 1f / Time.unscaledDeltaTime;
            }
            else
            {
                float fps = 1f / Time.unscaledDeltaTime;
                if (fps < min_fps) min_fps = fps;
            }

            string processingTimesInfo = "";
            if (timer > measureTime)
            {
                avg_fps = frames / timer;

                frames = 0;
                min_fps = 0f;
                timer = 0f;

                skelUpdates.totalUpdates = skelUpdates.updates;
                skelUpdates.updates = 0;

                userUpdates.totalUpdates = userUpdates.updates;
                userUpdates.updates = 0;

                handUpdates.totalUpdates = handUpdates.updates;
                handUpdates.updates = 0;
            }

            if (NuitrackManager.sensorsData.Count == 0)
                return;

            if (showNuitrackInfo)
            {
                processingTimesInfo += "hFOV: " + (NuitrackManager.sensorsData[0].DepthSensor.GetOutputMode().HFOV * Mathf.Rad2Deg).ToString("f1") + "\n";

                processingTimesInfo += "APP FPS: " + avg_fps.ToString("f2") + "\n";

                if (NuitrackManager.sensorsData[0].userTrackerModuleOn)
                {
                    if (NuitrackManager.sensorsData[0].UserFrame != null && NuitrackManager.sensorsData[0].UserFrame.Timestamp > userUpdates.lastTimeStamp)
                    {
                        ++userUpdates.updates;
                        userUpdates.lastTimeStamp = NuitrackManager.sensorsData[0].UserFrame.Timestamp;
                    }

                    processingTimesInfo += "User FPS: " + userUpdates.totalUpdates.ToString("f2") + "\n";
                }

                if (NuitrackManager.sensorsData[0].skeletonTrackerModuleOn)
                {
                    if (NuitrackManager.sensorsData[0].SkeletonTracker.GetSkeletonData().Timestamp > skelUpdates.lastTimeStamp)
                    {
                        ++skelUpdates.updates;
                        skelUpdates.lastTimeStamp = NuitrackManager.sensorsData[0].SkeletonTracker.GetSkeletonData().Timestamp;
                    }

                    processingTimesInfo += "Skeleton FPS: " + skelUpdates.totalUpdates.ToString("f2") + "\n";
                }

                if (NuitrackManager.sensorsData[0].handsTrackerModuleOn)
                {
                    if (NuitrackManager.sensorsData[0].HandTracker.GetHandTrackerData().Timestamp > handUpdates.lastTimeStamp)
                    {
                        ++handUpdates.updates;
                        handUpdates.lastTimeStamp = NuitrackManager.sensorsData[0].HandTracker.GetHandTrackerData().Timestamp;
                    }
                    processingTimesInfo += "Hand FPS: " + handUpdates.totalUpdates.ToString("f2") + "\n";
                }
            }

            tm.text = processingTimesInfo;
        }
    }
}