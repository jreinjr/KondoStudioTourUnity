using System;
using System.Collections;
using System.Collections.Generic;
using NuitrackSDK;
using UnityEngine;

public class HandTest : MonoBehaviour
{
    // Start is called before the first frame update
    void Start()
    {
        
    }

    public float HandAngle(nuitrack.Skeleton skeleton, UserData.Hand hand, nuitrack.JointType parentJoint, nuitrack.JointType childJoint)
    {
        Vector3 targetPosition = hand.Position;
        Vector3 parentPosition = skeleton.GetJoint(parentJoint).ToVector3();
        Vector3 childPosition = skeleton.GetJoint(childJoint).ToVector3();

        return Vector2.SignedAngle(parentPosition - targetPosition, childPosition - targetPosition);
    }

    // Update is called once per frame
    void Update()
    {
        UserData user = NuitrackManager.sensorsData[0].Users.Current;

        //you can get user.LeftHand or user.RightHand data
        if (user?.LeftHand != null)
            Debug.Log((float)Math.Round(HandAngle(user.Skeleton.RawSkeleton, user.LeftHand, nuitrack.JointType.LeftWrist, nuitrack.JointType.LeftElbow)));
    }
}
