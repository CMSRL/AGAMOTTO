using Ride.AI;
using Ride.Entities;
using Ride.Movement;
using System.Collections;
using System.Collections.Generic;
using static UnityEngine.GraphicsBuffer;

namespace Ride.Examples
{
    public class TestScript : RideBaseMinimal
    {
        public WaypointMono[] m_path;

        List<AgentGroupBehaviour> behaviours = new List<AgentGroupBehaviour>
        {
            
            new SurroundUsingMembers() { target = RideVector3.zero, radius = 15, includeMembersInSubgroups = true, includeMembersRecursively = true },
            new SurroundUsingSubgroups() { target = RideVector3.zero, radius = 15},
            new SquadFile(),
            new GroupMoveToPositionInFormationBehaviour() { destination = new RideVector3(0, 0, 0), formationDistance = 0.5f, formationType = FormationProcedureType.Column}
        }; 


        protected override void Start()
        {
            base.Start();

            Globals.api.attackSystem.flags = Combat.AttackSystemFlags.Auto_Attack;

            RideID alphaTeamAlphaSquad = Globals.api.agentSystem.CreateFireTeam(new UnitCreationParams("Alpha Team, Alpha Squad") { rows = 2, cols = 2, position = new RideVector3(25, 0, 15), rotation = RideQuaternion.Euler(0, -90, 0), team = Team.Blue });
            RideID bravoTeamAlphaSquad = Globals.api.agentSystem.CreateFireTeam(new UnitCreationParams("Bravo Team, Alpha Squad") { rows = 2, cols = 2, position = new RideVector3(25, 0, 15), rotation = RideQuaternion.Euler(0, -90, 0), team = Team.Blue });
            //RideID alphaSquadGlobals.api.agentSystem.CreateSquad(new UnitCreationParams("Alpha Squad") { rows = 2, cols = 2, position = new RideVector3(25, 0, 15), rotation = RideQuaternion.Euler(0, -90, 0), team = Team.Blue });
            //RideID betaSquad = Globals.api.agentSystem.CreateSquad(new UnitCreationParams("Beta Squad") { rows = 2, cols = 2, position = new RideVector3(-25, 0, -5), rotation = RideQuaternion.Euler(0, 90, 0), team = Team.Blue });
            //RideID gammaSquad = Globals.api.agentSystem.CreateSquad(new UnitCreationParams("Gamma Squad") { rows = 2, cols = 2, position = new RideVector3(-10, 0, -25), rotation = RideQuaternion.Euler(0, 180, 0), team = Team.Blue });
            //RideID deltaSquad = Globals.api.agentSystem.CreateSquad(new UnitCreationParams("Delta Squad") { rows = 2, cols = 2, position = new RideVector3(25, 0, 5), rotation = RideQuaternion.Euler(0, 0, 0), team = Team.Blue });
            RideID insurgents = Globals.api.agentSystem.CreateSquad(new UnitCreationParams("Insurgents") { rows = 2, cols = 2, position = new RideVector3(0, 0, 0), rotation = RideQuaternion.Euler(0, 0, 0), team = Team.Red });
            RideID alphaSquad = Globals.api.groupSystem.CreateGroup("Force", null, new RideID[] { alphaTeamAlphaSquad, bravoTeamAlphaSquad});
            //RideID platoon = Globals.api.groupSystem.CreateGroup("Force", null, new RideID[] { alphaSquad, deltaSquad, betaSquad, gammaSquad, });
            Globals.api.agentSystem.SetAgentGroupBehaviour(alphaTeamAlphaSquad, new FireTeamWedge());
            Globals.api.agentSystem.SetAgentGroupBehaviour(bravoTeamAlphaSquad, new FireTeamWedge());
            Globals.api.agentSystem.SetAgentGroupBehaviour(alphaSquad, new GroupMoveToPositionInFormationBehaviour() { destination = new RideVector3(0, 0, 0), formationDistance = 0.5f, formationType = FormationProcedureType.MaintainRelativeDistance});


        }


    }
}