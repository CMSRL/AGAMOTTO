using Ride;
using Ride.AI.RL;
using Ride.Examples;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class AGAMOTTOMLTraining : RideBaseMinimal
{
    public UnityEngine.GameObject m_settingsPrefabGameObject;

    protected override void Start()
    {
        base.Start();

        Globals.api.attackSystem.flags = Ride.Combat.AttackSystemFlags.Acquire_Targets;


    }
}
