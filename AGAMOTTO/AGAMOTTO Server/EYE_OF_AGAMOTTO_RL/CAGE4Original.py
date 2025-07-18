#%%
#%%
# --------------------------------------------------
# Author: Amirhossein Ravari
# Email: ravari.a@northeastern.edu
# Description: This code was written to implement CAGE4 Environment
# Please feel free to contact me via email for any
# questions or further details regarding the code.
# --------------------------------------------------

import random
import networkx as nx

# Define the network partitions and system states
NETWORK_PARTITIONS = ["Contractor Network",
                      "Deployed Network A", "Operational Zone A", "Restricted Zone A",
                      "Deployed Network B", "Operational Zone B", "Restricted Zone B",
                      "Headquarters Network", "Public Access Zone", "Office Network", "Admin Network"]

STATE_UNKNOWN = 2
STATE_COMPROMISED = 1
STATE_SAFE = 0

# Define network status dictionary
network_status = {
    "Contractor Network": 1,
    "Deployed Network A": 1,
    "Deployed Network B": 1,
    "Headquarters Network": 1
}

# Define reward mapping for zones
ZONE_REWARDS = {
    "Headquarters Network": {
        "Local Work Fails": -1,
        "Access Service Fails": -1,  # Used for failed transfers
        "Red Impact/Access": -3,
        "Access Control": -1,
        "Server Compromise": -1  # Same as Local Work Fails
    },
    "Public Access Zone": {
        "Local Work Fails": -1,
        "Access Service Fails": -1,
        "Red Impact/Access": -3,
        "Access Control": -1,
        "Server Compromise": -1
    },
    "Office Network": {
        "Local Work Fails": -1,
        "Access Service Fails": -1,
        "Red Impact/Access": -3,
        "Access Control": -1,
        "Server Compromise": -1
    },
    "Admin Network": {
        "Local Work Fails": -1,
        "Access Service Fails": -1,
        "Red Impact/Access": -3,
        "Access Control": -1,
        "Server Compromise": -1
    },
    "Contractor Network": {
        "Local Work Fails": 0,
        "Access Service Fails": 0,
        "Red Impact/Access": 0,
        "Access Control": 0,
        "Server Compromise": 0
    },
    "Restricted Zone A": {
        "Local Work Fails": -1,
        "Access Service Fails": -3,  # Note the value is -3 here
        "Red Impact/Access": -1,
        "Access Control": -1,
        "Server Compromise": -1
    },
    "Operational Zone A": {
        "Local Work Fails": -1,
        "Access Service Fails": -1,
        "Red Impact/Access": -1,
        "Access Control": -1,
        "Server Compromise": -1
    },
    "Restricted Zone B": {
        "Local Work Fails": -1,
        "Access Service Fails": -3,  # Note the value is -3 here
        "Red Impact/Access": -1,
        "Access Control": -1,
        "Server Compromise": -1
    },
    "Operational Zone B": {
        "Local Work Fails": -1,
        "Access Service Fails": -1,
        "Red Impact/Access": -1,
        "Access Control": -1,
        "Server Compromise": -1
    }
}

class Agent:
    def __init__(self, agent_id, network_zone):
        self.access_control = False
        self.agent_id = agent_id
        self.network_zone = network_zone
        self.compromised = False
        self.receive_buffer = []  # List for tracking incoming messages

    def get_id(self):
        return self.agent_id

class GreenAgent(Agent):
    def __init__(self, agent_id, network_zone, security_risk):
        super().__init__(agent_id, network_zone)
        self.action_list = ["Local work", "Open email", "Transfer Information"]
        self.security_risk = security_risk  # Probability of compromise when opening email

    def open_email(self):
        is_malicious = random.random() < self.security_risk
        if is_malicious:
            self.compromised = True
            return f"{self.agent_id} opened a malicious email and was compromised."
        return f"{self.agent_id} opened an email safely."

    def perform_local_work(self):
        return f"{self.agent_id} is performing local work."

    def schedule_transfer(self, target_agent, distance):
        if distance == float('inf'):
            # Retrieve the "Access Service Fails" reward from ZONE_REWARDS based on the agent's network zone
            zone_reward = ZONE_REWARDS.get(self.network_zone, {})
            reward = zone_reward.get("Access Service Fails", 0)
            return f"{self.agent_id} failed to transfer information to {target_agent.agent_id} due to no path.", reward
        else:
            # Include the sender's compromised status at the time of sending
            message = {
                'sender': self.agent_id,
                'sender_compromised': self.compromised,
                'delay': distance
            }
            target_agent.receive_buffer.append(message)
            return f"{self.agent_id} scheduled information transfer to {target_agent.agent_id} after {distance} timesteps.", None

class RedAgent(Agent):
    def __init__(self, success_rate):
        super().__init__("RedAgent", "Contractor Network")
        self.positions = []  # List of compromised green agents' positions
        self.action_list = ["Attack Server", "Transfer Information", "Sleep"]
        self.success_rate = success_rate  # Probability of successful server attack

    def add_position(self, compromised_agent):
        if compromised_agent not in self.positions:
            self.positions.append(compromised_agent)

    def attack_server(self, servers):
        available_servers = [server for server in servers if not server.compromised]
        if available_servers and random.random() < self.success_rate:
            target_server = random.choice(available_servers)
            target_server.compromised = True
            return f"RedAgent successfully compromised {target_server.server_id}.", target_server
        return f"RedAgent failed to compromise a server.", None

    def schedule_transfer(self, sender_agent, target_agent, distance):
        if distance == float('inf'):
            # No negative reward for red agent's failed transfer
            return f"RedAgent failed to transfer information from {sender_agent.agent_id} to {target_agent.agent_id} due to no path.", None
        else:
            # Include the sender's compromised status (should be True)
            message = {
                'sender': sender_agent.agent_id,
                'sender_compromised': sender_agent.compromised,
                'delay': distance
            }
            target_agent.receive_buffer.append(message)
            return f"RedAgent scheduled information transfer from {sender_agent.agent_id} to {target_agent.agent_id} after {distance} timesteps.", None

class BlueAgent(Agent):
    def __init__(self, agent_id, network_zone, subnet_type):
        super().__init__(agent_id, network_zone)
        self.reboot_limit = 2
        self.subnet_type = subnet_type
        self.action_mapping = self._create_action_mapping()
        self.decoy_targets = {}
        self.observation_order = self._get_observation_order()

    def _get_observation_order(self):
        if self.subnet_type == "Headquarters":
            # Observation order for HQ defender
            observation_order = []
            zones = ["Public Access Zone", "Office Network", "Admin Network"]
            for zone in zones:
                for i in range(3):
                    observation_order.append(f"GreenAgent-{zone}-{i+1}")
                for i in range(3):
                    observation_order.append(f"Server-{zone}-{i+1}")
            return observation_order
        else:
            # Observation order for deployed networks
            observation_order = []
            for i in range(3):
                observation_order.append(f"GreenAgent-{self.network_zone}-{i+1}")
            for i in range(3):
                observation_order.append(f"Server-{self.network_zone}-{i+1}")
            return observation_order

    def _create_action_mapping(self):
        actions = self.action_space()
        action_mapping = {i: action for i, action in enumerate(actions)}
        return action_mapping

    def monitor(self):
        return f"{self.agent_id} is monitoring the {self.network_zone}."

    def reboot(self, target):
        target_id = target.get_id()
        if target.compromised:
            target.compromised = False  # Change state to safe
            return f"{self.agent_id} rebooted {target_id}."
        else:
            return f"{target_id} was not compromised."


    def deploy_decoy(self, target):
        # Deploy decoy on target agent for 1 step
        self.decoy_targets[target.agent_id] = 1  # Decoy lasts for 1 step
        return f"{self.agent_id} deployed decoy on {target.agent_id}."

    def update_decoys(self):
        # Decrease decoy timers and remove expired decoys
        expired_decoys = [agent_id for agent_id, steps in self.decoy_targets.items() if steps <= 0]
        for agent_id in expired_decoys:
            del self.decoy_targets[agent_id]
        # Decrease the decoy steps for active decoys
        for agent_id in self.decoy_targets:
            self.decoy_targets[agent_id] -= 1

    def action_space(self):
        actions = []

        if self.subnet_type == "Headquarters":
            # Specific green agents and servers in each HQ subnetwork
            zones = ["Public Access Zone", "Office Network", "Admin Network"]
            green_agents_by_zone = {zone: [f"GreenAgent-{zone}-{i+1}" for i in range(3)] for zone in zones}
            servers_by_zone = {zone: [f"Server-{zone}-{i+1}" for i in range(3)] for zone in zones}

            # Flatten lists of agents and servers
            all_green_agents = [agent for agents in green_agents_by_zone.values() for agent in agents]
            all_servers = [server for servers in servers_by_zone.values() for server in servers]

            # Reboot single agents
            for ga in all_green_agents:
                actions.append(f"Reboot {ga}")

            # Reboot single servers
            for server in all_servers:
                actions.append(f"Reboot {server}")

            # Reboot two agents (from the same zone)
            for zone in zones:
                green_agents = green_agents_by_zone[zone]
                for i, ga1 in enumerate(green_agents):
                    for ga2 in green_agents[i+1:]:
                        actions.append(f"Reboot {ga1} and {ga2}")

            # Reboot two servers (from the same zone)
            for zone in zones:
                servers = servers_by_zone[zone]
                for i, server1 in enumerate(servers):
                    for server2 in servers[i+1:]:
                        actions.append(f"Reboot {server1} and {server2}")

            # Reboot one agent and one server (from the same zone)
            for zone in zones:
                green_agents = green_agents_by_zone[zone]
                servers = servers_by_zone[zone]
                for ga in green_agents:
                    for server in servers:
                        actions.append(f"Reboot {ga} and {server}")

            # Monitoring action
            actions.append(f"Monitor {self.network_zone}")

            # Enable/Disable Access Control actions
            for ga in all_green_agents:
                actions.append(f"Enable Access Control on {ga}")
                actions.append(f"Disable Access Control on {ga}")

            # Add Decoy actions
            for target in all_green_agents:
                actions.append(f"Deploy Decoy on {target}")

        else:
            # For Deployed Networks
            green_agents = [f"GreenAgent-{self.network_zone}-{i+1}" for i in range(3)]
            servers = [f"Server-{self.network_zone}-{i+1}" for i in range(3)]

            # Reboot single agents
            for ga in green_agents:
                actions.append(f"Reboot {ga}")

            # Reboot single servers
            for server in servers:
                actions.append(f"Reboot {server}")

            # Reboot two agents
            for i, ga1 in enumerate(green_agents):
                for ga2 in green_agents[i+1:]:
                    actions.append(f"Reboot {ga1} and {ga2}")

            # Reboot two servers
            for i, server1 in enumerate(servers):
                for server2 in servers[i+1:]:
                    actions.append(f"Reboot {server1} and {server2}")

            # Reboot one agent and one server
            for ga in green_agents:
                for server in servers:
                    actions.append(f"Reboot {ga} and {server}")

            # Monitoring action
            actions.append(f"Monitor {self.network_zone}")

            # Enable/Disable Access Control actions
            for ga in green_agents:
                actions.append(f"Enable Access Control on {ga}")
                actions.append(f"Disable Access Control on {ga}")

            # Add Decoy actions
            for target in green_agents:
                actions.append(f"Deploy Decoy on {target}")

        return actions

class Server:
    def __init__(self, server_id, network_zone):
        self.server_id = server_id
        self.network_zone = network_zone
        self.compromised = False

    def get_id(self):
        return self.server_id

class CAGE4Environment:
    def __init__(self, security_risk=0.4, success_rate=0.4, debug=False):
        self.debug = debug
        self.graph = nx.Graph()
        self.green_agents = {}
        self.red_agent = RedAgent(success_rate)
        self.blue_agents = {}
        self.servers = {}
        self.step_count = 0
        self.step_threshold = 1000
        self.security_risk = security_risk
        self.true_state = {}

    def _debug_print(self, message):
        if self.debug:
            print(message)

    def reset(self):
        # Clear all data structures
        self.graph.clear()
        self.green_agents.clear()
        self.servers.clear()
        self.blue_agents.clear()
        self.true_state.clear()
        self.red_agent.positions.clear()
    
        # Setup the environment anew
        self.setup_environment()
        self._initialize_true_state()
    
        # Reset network status
        for partition in network_status:
            network_status[partition] = 1
    
        self.step_count = 0  # Reset step count
    
        # Add blue agents
        self.blue_agents["BlueAgent-OperationalZoneA"] = BlueAgent("BlueAgent-OperationalZoneA", "Operational Zone A", "Deployed")
        self.blue_agents["BlueAgent-RestrictedZoneA"] = BlueAgent("BlueAgent-RestrictedZoneA", "Restricted Zone A", "Deployed")
        self.blue_agents["BlueAgent-OperationalZoneB"] = BlueAgent("BlueAgent-OperationalZoneB", "Operational Zone B", "Deployed")
        self.blue_agents["BlueAgent-RestrictedZoneB"] = BlueAgent("BlueAgent-RestrictedZoneB", "Restricted Zone B", "Deployed")
        self.blue_agents["BlueAgent-HQ"] = BlueAgent("BlueAgent-HQ", "Headquarters Network", "Headquarters")
    
        observations = {}
        info = {}
        for blue_agent_id, blue_agent in self.blue_agents.items():
            observation = []
            for entity_id in blue_agent.observation_order:
                observation.append(self.true_state.get(entity_id, STATE_UNKNOWN))
            observations[blue_agent_id] = observation
    
        return observations, info


    def setup_environment(self):
        # Adding the cloud node as the root
        self.graph.add_node("Cloud")

        # Setting up the main network nodes and edges to the cloud
        for partition in ["Contractor Network", "Deployed Network A", "Deployed Network B", "Headquarters Network"]:
            self.graph.add_node(partition)
            self.graph.add_edge("Cloud", partition)

        # Setting up subnetwork zones for each deployed network and headquarters with walls as separate nodes
        self._add_wall("Deployed Network A", "Restricted Zone A", "Wall A")
        self._setup_zones("Deployed Network A", ["Operational Zone A"])

        self._add_wall("Deployed Network B", "Restricted Zone B", "Wall B")
        self._setup_zones("Deployed Network B", ["Operational Zone B"])

        self._add_wall("Headquarters Network", "Office Network", "Wall Office")
        self._add_wall("Headquarters Network", "Admin Network", "Wall Admin")

        self._setup_zones("Headquarters Network", ["Public Access Zone"])

        # Adding agents and servers as nodes under each zone
        for zone in ["Contractor Network", "Operational Zone A", "Restricted Zone A", "Operational Zone B",
                     "Restricted Zone B", "Public Access Zone", "Office Network", "Admin Network"]:
            self._add_agents_and_servers(zone)

        # Set the first green agent in Contractor Network as compromised
        contractor_agents = [agent for agent in self.green_agents.values() if agent.network_zone == "Contractor Network"]
        if contractor_agents:
            compromised_agent = contractor_agents[0]
            compromised_agent.compromised = True
            self.red_agent.add_position(compromised_agent)


    def _setup_zones(self, parent, zones):
        for zone in zones:
            self.graph.add_node(zone)
            self.graph.add_edge(parent, zone)

    def _add_wall(self, network, zone, wall_name):
        self.graph.add_node(wall_name)
        self.graph.add_node(zone)
        self.graph.add_edge(network, wall_name)
        self.graph.add_edge(zone, wall_name)

    def _add_agents_and_servers(self, zone):
        for i in range(3):
            green_agent = GreenAgent(f"GreenAgent-{zone}-{i+1}", zone, self.security_risk)
            self.green_agents[green_agent.agent_id] = green_agent
            self.graph.add_node(green_agent.agent_id)
            self.graph.add_edge(green_agent.agent_id, zone)

            server = Server(f"Server-{zone}-{i+1}", zone)
            self.servers[server.server_id] = server
            self.graph.add_node(server.server_id)
            self.graph.add_edge(server.server_id, zone)

    def _initialize_true_state(self):
        for agent_id in self.green_agents:
            self.true_state[agent_id] = STATE_SAFE
        for server_id in self.servers:
            self.true_state[server_id] = STATE_SAFE

        # Update the state of compromised agents
        for agent in self.green_agents.values():
            if agent.compromised:
                self.true_state[agent.agent_id] = STATE_COMPROMISED

    def step(self, blue_agent_actions):
        observations = {}
        rewards = {}
        term = {}
        trunc = {}
        info = {}
        accumulated_reward = 0  # Total reward accumulated in this step

        self.update_true_state()

        self.step_count += 1

        # Update edges for nodes with access control enabled
        for green_agent in list(self.green_agents.values()):
            if green_agent.access_control:
                if self.graph.has_edge(green_agent.agent_id, green_agent.network_zone):
                    self.graph.remove_edge(green_agent.agent_id, green_agent.network_zone)
            else:
                if not self.graph.has_edge(green_agent.agent_id, green_agent.network_zone):
                    self.graph.add_edge(green_agent.agent_id, green_agent.network_zone)

        # Update edges based on network status
        for partition, status in network_status.items():
            if status == 0 and self.graph.has_edge("Cloud", partition):
                self.graph.remove_edge("Cloud", partition)
            elif status == 1 and not self.graph.has_edge("Cloud", partition):
                self.graph.add_edge("Cloud", partition)

        # Step 1: Green Agents take action
        for green_agent in self.green_agents.values():
            # Initialize agent's reward
            agent_reward = 0

            if green_agent.compromised:
                # Agent's actions fail due to being compromised
                zone_reward = ZONE_REWARDS.get(green_agent.network_zone, {})
                reward = zone_reward.get("Local Work Fails", 0)
                agent_reward += reward
                accumulated_reward += reward
                self._debug_print(f"{green_agent.agent_id} failed to perform actions due to being compromised.")
                continue  # Agent cannot perform any action

            if green_agent.access_control:
                # Agent cannot perform actions due to access control
                zone_reward = ZONE_REWARDS.get(green_agent.network_zone, {})
                ac_reward = zone_reward.get("Access Control", 0)
                agent_reward += ac_reward
                accumulated_reward += ac_reward
                self._debug_print(f"{green_agent.agent_id} failed to perform actions due to access control.")
                continue  # Agent cannot perform any action

            action = random.choice(green_agent.action_list)

            if action == "Open email":
                result = green_agent.open_email()
                if green_agent.compromised:
                    self.red_agent.add_position(green_agent)  # Add compromised green agent to red agent's positions
                    # No immediate reward change for compromise here
                self._debug_print(result)
            elif action == "Transfer Information":
                target_agent = random.choice(list(self.green_agents.values()))
                distance = self.calculate_distance(green_agent.agent_id, target_agent.agent_id)
                result, reward = green_agent.schedule_transfer(target_agent, distance)
                if reward is not None:
                    agent_reward += reward
                    accumulated_reward += reward
                self._debug_print(result)
            else:
                # Local work succeeds
                self._debug_print(f"{green_agent.agent_id} is performing local work.")
                # No reward change for successful local work

        # Step 2: Process receive buffers for all agents
        for agent in self.green_agents.values():
            if agent.access_control:
                self._debug_print(f"{agent.agent_id} cannot receive messages due to access control.")
                continue  # Skip processing receive buffer

            new_buffer = []
            for message in agent.receive_buffer:
                message['delay'] -= 1
                if message['delay'] == 0:
                    sender_id = message['sender']
                    sender_compromised = message['sender_compromised']  # Use stored sender status
                    # Check if path exists between sender and receiver at time of delivery
                    if self.calculate_distance(sender_id, agent.agent_id) != float('inf'):
                        # Path exists
                        if agent.compromised:
                            # Receiver is compromised at time of delivery
                            if sender_id.startswith("GreenAgent"):
                                # Apply Access Service Fails penalty for sender's subnet
                                sender_agent = self.green_agents.get(sender_id)
                                if sender_agent:
                                    zone_reward = ZONE_REWARDS.get(sender_agent.network_zone, {})
                                    reward = zone_reward.get("Access Service Fails", 0)
                                    accumulated_reward += reward
                            self._debug_print(f"{agent.agent_id} is compromised and cannot receive the message from {sender_id}.")
                            continue
                        if sender_compromised:
                            # Check for decoy
                            decoy_active = any(agent.agent_id in blue_agent.decoy_targets for blue_agent in self.blue_agents.values())
                            if decoy_active:
                                # Attack is prevented and reversed
                                self._debug_print(f"Attack on {agent.agent_id} was prevented by decoy.")
                                # No compromise occurs
                            else:
                                # Compromised sender compromises the receiver
                                agent.compromised = True
                                self.red_agent.add_position(agent)
                                self._debug_print(f"{agent.agent_id} was compromised by {sender_id}.")

                                # Apply red impact reward
                                zone_reward = ZONE_REWARDS.get(agent.network_zone, {})
                                reward = zone_reward.get("Red Impact/Access", 0)
                                accumulated_reward += reward
                        else:
                            # Successful information transfer
                            self._debug_print(f"{agent.agent_id} successfully received information from {sender_id}.")
                    else:
                        # No path exists at time of delivery
                        if sender_id.startswith("GreenAgent"):
                            # Apply Access Service Fails reward for sender's subnet
                            sender_agent = self.green_agents.get(sender_id)
                            if sender_agent:
                                zone_reward = ZONE_REWARDS.get(sender_agent.network_zone, {})
                                reward = zone_reward.get("Access Service Fails", 0)
                                accumulated_reward += reward
                                self._debug_print(f"{agent.agent_id} failed to receive information from {sender_id} due to no path.")
                        else:
                            # No negative reward for failed transfers from red agents
                            self._debug_print(f"{agent.agent_id} failed to receive information from {sender_id} due to no path.")
                else:
                    new_buffer.append(message)
            agent.receive_buffer = new_buffer

        # Step 3: Red Agent takes action (attacker)
        for position in self.red_agent.positions:
            if position.access_control:
                self._debug_print(f"Red agent cannot use {position.agent_id} due to access control.")
                continue  # Cannot use this compromised agent

            action = random.choice(self.red_agent.action_list)
            if action == "Attack Server":
                servers_in_zone = [server for server in self.servers.values() if server.network_zone == position.network_zone]
                result, compromised_server = self.red_agent.attack_server(servers_in_zone)
                self._debug_print(result)
                if compromised_server:
                    zone_reward = ZONE_REWARDS.get(compromised_server.network_zone, {})
                    reward = zone_reward.get("Server Compromise", 0)
                    accumulated_reward += reward
            elif action == "Transfer Information":
                available_green_agents = [agent for agent in self.green_agents.values() if not agent.compromised and not agent.access_control]
                if available_green_agents:
                    target_agent = random.choice(available_green_agents)
                    distance = self.calculate_distance(position.agent_id, target_agent.agent_id)
                    result, _ = self.red_agent.schedule_transfer(position, target_agent, distance)
                    self._debug_print(result)
            elif action == "Sleep":
                self._debug_print(f"RedAgent at {position.agent_id} is sleeping.")
                # Do nothing
            else:
                self._debug_print(f"Unknown action '{action}' by RedAgent at {position.agent_id}.")

        # Step 4: Blue Agents take action (defender)
        for blue_agent_id, action_number in blue_agent_actions.items():
            blue_agent = self.blue_agents.get(blue_agent_id)
            if blue_agent is None:
                continue

            # Get the action string from the action number
            action = blue_agent.action_mapping.get(action_number)
            if action is None:
                self._debug_print(f"Invalid action number {action_number} for {blue_agent_id}")
                continue

            # Deduct action cost
            if not action.startswith("Monitor"):
                accumulated_reward -= 1  # Each blue agent action costs -1

            # Observation for defenders
            if action.startswith("Monitor"):
                observation = []
                for entity_id in blue_agent.observation_order:
                    observation.append(self.true_state.get(entity_id, STATE_UNKNOWN))
                observations[blue_agent_id] = observation

            elif action.startswith("Deploy Decoy"):
                action_parts = action.split(" on ")
                if len(action_parts) == 2:
                    target_id = action_parts[1]
                    observation = [STATE_UNKNOWN] * len(blue_agent.observation_order)
                    if target_id in blue_agent.observation_order:
                        index = blue_agent.observation_order.index(target_id)
                        observation[index] = self.true_state.get(target_id, STATE_UNKNOWN)
                    else:
                        self._debug_print(f"Target entity {target_id} not in observed entities for {blue_agent_id}.")
                    observations[blue_agent_id] = observation

                    # Deploy decoy
                    target_agent = self.green_agents.get(target_id)
                    if target_agent:
                        result = blue_agent.deploy_decoy(target_agent)
                        self._debug_print(result)
                    else:
                        self._debug_print(f"Target agent {target_id} not found.")
                else:
                    self._debug_print(f"Invalid Deploy Decoy action format by {blue_agent_id}.")
                    observations[blue_agent_id] = [STATE_UNKNOWN] * len(blue_agent.observation_order)
            elif action.startswith("Reboot"):
                action_parts = action.split(' ', 1)
                if len(action_parts) > 1:
                    targets_str = action_parts[1]
                    target_ids = [tid.strip() for tid in targets_str.split(' and ')]
                    for target_id in target_ids:
                        target_agent = self.green_agents.get(target_id)
                        target_server = self.servers.get(target_id)
                        if target_agent:
                            result = blue_agent.reboot(target_agent)
                            self._debug_print(result)
                        elif target_server:
                            result = blue_agent.reboot(target_server)
                            self._debug_print(result)
                        else:
                            self._debug_print(f"Target {target_id} not found.")
                else:
                    self._debug_print(f"No targets specified for reboot action by {blue_agent_id}.")
                observations[blue_agent_id] = [STATE_UNKNOWN] * len(blue_agent.observation_order)
            elif action.startswith("Enable Access Control"):
                target_id = action.split(" on ")[1]
                target_agent = self.green_agents.get(target_id)
                if target_agent:
                    target_agent.access_control = True
                    self._debug_print(f"{blue_agent.agent_id} enabled access control on {target_agent.agent_id}.")
                else:
                    self._debug_print(f"Target agent {target_id} not found.")
                observations[blue_agent_id] = [STATE_UNKNOWN] * len(blue_agent.observation_order)
            elif action.startswith("Disable Access Control"):
                target_id = action.split(" on ")[1]
                target_agent = self.green_agents.get(target_id)
                if target_agent:
                    target_agent.access_control = False
                    self._debug_print(f"{blue_agent.agent_id} disabled access control on {target_agent.agent_id}.")
                else:
                    self._debug_print(f"Target agent {target_id} not found.")
                observations[blue_agent_id] = [STATE_UNKNOWN] * len(blue_agent.observation_order)
            else:
                self._debug_print(f"Unknown action '{action}' by {blue_agent_id}")
                observations[blue_agent_id] = [STATE_UNKNOWN] * len(blue_agent.observation_order)

        # After processing all actions, update decoy timers
        for blue_agent in self.blue_agents.values():
            blue_agent.update_decoys()

        # Step 5: Handle termination and truncation logic based on step threshold
        for blue_agent_id in blue_agent_actions:
            if self.step_count >= self.step_threshold:
                term[blue_agent_id] = True
                trunc[blue_agent_id] = True
            else:
                term[blue_agent_id] = False
                trunc[blue_agent_id] = False

        # Assign the accumulated reward to all blue agents (shared reward)
        rewards = {agent_id: accumulated_reward for agent_id in self.blue_agents}

        # Display the true state vector for debugging
        true_state_vector = self.generate_state_vector()
        self._debug_print("True State Vector (for debugging):")
        self._debug_print(true_state_vector)

        return observations, rewards, term, trunc, info

    def update_true_state(self):
        for agent_id, agent in self.green_agents.items():
            if agent.compromised:
                self.true_state[agent_id] = STATE_COMPROMISED
            else:
                self.true_state[agent_id] = STATE_SAFE

        for server_id, server in self.servers.items():
            if server.compromised:
                self.true_state[server_id] = STATE_COMPROMISED
            else:
                self.true_state[server_id] = STATE_SAFE

    def generate_state_vector(self):
        # Generate the true state vector for all blue agents
        vector = {}
        for blue_agent_id, blue_agent in self.blue_agents.items():
            observations = []
            for entity_id in blue_agent.observation_order:
                observations.append(self.true_state.get(entity_id, STATE_UNKNOWN))
            vector[blue_agent_id] = observations

        # Include Contractor Network agents and servers
        contractor_entities = []
        contractor_agents = [f"GreenAgent-Contractor Network-{i+1}" for i in range(3)]
        contractor_servers = [f"Server-Contractor Network-{i+1}" for i in range(3)]
        for entity_id in contractor_agents + contractor_servers:
            contractor_entities.append(self.true_state.get(entity_id, STATE_UNKNOWN))
        vector["Contractor Network"] = contractor_entities

        return vector

    def calculate_distance(self, node_a, node_b):
        try:
            distance = nx.shortest_path_length(self.graph, source=node_a, target=node_b)
            return distance
        except nx.NetworkXNoPath:
            return float('inf')  # Return infinity if there is no path between nodes

    def display_network(self):
        import matplotlib.pyplot as plt
        plt.figure(figsize=(12, 8))
        pos = nx.spring_layout(self.graph, seed=42)
        nx.draw_networkx(self.graph, pos, with_labels=True, node_color='lightblue', edge_color='gray', font_size=8)
        plt.title("Network Graph")
        plt.axis('off')
        plt.show()

# Example usage
env = CAGE4Environment(security_risk=0.4, success_rate=0.4, debug=False)

# Reset the environment and get the initial state vector
obs, _ = env.reset()

# Optionally display the network graph
env.display_network()

# Collect and display action mappings for each blue agent
for agent_id, blue_agent in env.blue_agents.items():
    print(f"Action Mapping for {agent_id}:")
    for number, action in blue_agent.action_mapping.items():
        print(f"  {number}: {action}")
    print()

# Define actions for each blue agent using action numbers
blue_agent_actions = {}

# For demonstration, let's have each agent perform a "Monitor" action
for agent_id, blue_agent in env.blue_agents.items():
    for number, action in blue_agent.action_mapping.items():
        if action.startswith("Monitor"):
            blue_agent_actions[agent_id] = number
            break

# Take 5 consecutive steps in the environment
for step_num in range(5):
    observations, rewards, term, trunc, info = env.step(blue_agent_actions)

    print(f"Step {step_num + 1}:")
    print("Observations:", observations)
    #for agent_id, observation in observations.items():
    #    print(f"  {agent_id}: {observation}")
    print("Rewards:", rewards)
    print("Terminations:", term)
    print("Truncations:", trunc)
    print("Info:", info)
    print()

#%%
import math
import random
import matplotlib
import matplotlib.pyplot as plt
from collections import namedtuple, deque
from itertools import count
import time
from tqdm import tqdm

import torch
import torch.nn as nn
import torch.optim as optim
import torch.nn.functional as F

# Set up matplotlib
is_ipython = 'inline' in matplotlib.get_backend()
if is_ipython:
    from IPython import display

plt.ion()

# Set up device
device = torch.device(
    "cuda" if torch.cuda.is_available() else
    "mps" if torch.backends.mps.is_available() else
    "cpu"
)

# Import your environment
# from your_environment_module import CAGE4Environment

# Initialize the environment
env = CAGE4Environment(security_risk=0.1, success_rate=0.4, debug=False)

# Define a named tuple for storing experiences
Transition = namedtuple('Transition', ('state', 'action', 'next_state', 'reward'))

class ReplayMemory(object):
    """Replay Memory to store past experiences for training"""

    def __init__(self, capacity):
        self.memory = deque([], maxlen=capacity)

    def push(self, *args):
        """Save a transition"""
        self.memory.append(Transition(*args))

    def sample(self, batch_size):
        """Sample a batch of experiences"""
        return random.sample(self.memory, batch_size)

    def __len__(self):
        return len(self.memory)

class DQN(nn.Module):
    """Deep Q-Network"""

    def __init__(self, n_observations, n_actions):
        super(DQN, self).__init__()
        self.layer1 = nn.Linear(n_observations, 128)
        self.layer2 = nn.Linear(128, 128)
        self.layer3 = nn.Linear(128, n_actions)

    def forward(self, x):
        x = F.relu(self.layer1(x))
        x = F.relu(self.layer2(x))
        return self.layer3(x)

# Initialize hyperparameters
BATCH_SIZE = 128
GAMMA = 0.99
EPS_START = 0.9
EPS_END = 0.05
EPS_DECAY = 1000
TAU = 0.005
LR = 1e-4

# Reset the environment and get initial observations
obs, info = env.reset()
agents = list(obs.keys())

# Initialize networks, optimizers, and memories for each agent
policy_nets = {agent: DQN(len(obs[agent]), len(env.blue_agents[agent].action_mapping)).to(device) for agent in agents}
target_nets = {agent: DQN(len(obs[agent]), len(env.blue_agents[agent].action_mapping)).to(device) for agent in agents}
optimizers = {agent: optim.AdamW(policy_nets[agent].parameters(), lr=LR, amsgrad=True) for agent in agents}
memories = {agent: ReplayMemory(10000) for agent in agents}

# Copy policy network parameters to target networks
for agent in agents:
    target_nets[agent].load_state_dict(policy_nets[agent].state_dict())

steps_done = {agent: 0 for agent in agents}

# Define action selection function
def select_action(state, agent):
    sample = random.random()
    eps_threshold = EPS_END + (EPS_START - EPS_END) * \
        math.exp(-1. * steps_done[agent] / EPS_DECAY)
    steps_done[agent] += 1
    if sample > eps_threshold:
        with torch.no_grad():
            return policy_nets[agent](state).max(1).indices.view(1, 1)
    else:
        num_actions = len(env.blue_agents[agent].action_mapping)
        return torch.tensor([[random.randint(0, num_actions - 1)]], device=device, dtype=torch.long)

# Plotting function
def plot_durations(reward_list, show_result=False):
    plt.figure(1)
    plt.clf()
    for agent in reward_list:
        rewards_t = torch.tensor(reward_list[agent], dtype=torch.float)
        if show_result:
            plt.title('Result')
        else:
            plt.title('Training...')
        plt.xlabel('Episode')
        plt.ylabel('Reward')
        plt.plot(rewards_t.numpy(), label=f"Agent {agent}")
        if len(rewards_t) >= 100:
            means = rewards_t.unfold(0, 100, 1).mean(1).view(-1)
            means = torch.cat((torch.zeros(99), means))
            plt.plot(means.numpy(), label=f"Agent {agent} (100-ep avg)")
    plt.legend()
    plt.pause(0.001)
    if is_ipython:
        if not show_result:
            display.display(plt.gcf())
            display.clear_output(wait=True)
        else:
            display.display(plt.gcf())

# Optimization function
def optimize_model(agent):
    if len(memories[agent]) < BATCH_SIZE:
        return
    transitions = memories[agent].sample(BATCH_SIZE)
    batch = Transition(*zip(*transitions))

    # Mask to filter out final states
    non_final_mask = torch.tensor(tuple(map(lambda s: s is not None,
                                            batch.next_state)), device=device, dtype=torch.bool)
    # Concatenate the non-final next states
    non_final_next_states = torch.cat([s for s in batch.next_state if s is not None])
    state_batch = torch.cat(batch.state)
    action_batch = torch.cat(batch.action)
    reward_batch = torch.cat(batch.reward)

    # Compute Q(s_t, a)
    state_action_values = policy_nets[agent](state_batch).gather(1, action_batch)

    # Compute V(s_{t+1})
    next_state_values = torch.zeros(BATCH_SIZE, device=device)
    with torch.no_grad():
        next_state_values[non_final_mask] = target_nets[agent](non_final_next_states).max(1).values
    # Compute expected Q values
    expected_state_action_values = (next_state_values * GAMMA) + reward_batch

    # Compute loss
    criterion = nn.SmoothL1Loss()
    loss = criterion(state_action_values, expected_state_action_values.unsqueeze(1))

    # Optimize the model
    optimizers[agent].zero_grad()
    loss.backward()
    torch.nn.utils.clip_grad_value_(policy_nets[agent].parameters(), 100)
    optimizers[agent].step()
    return loss.item()

# Training loop
if torch.cuda.is_available() or torch.backends.mps.is_available():
    num_episodes = 5000
else:
    num_episodes = 200

reward_list = {agent: [] for agent in agents}
start_time = time.time()
for i_episode in tqdm(range(num_episodes)):
    obs, info = env.reset()
    states = {agent: torch.tensor(obs[agent], dtype=torch.float32, device=device).unsqueeze(0) for agent in agents}
    temp_rewards = {agent: 0 for agent in agents}
    MAX_T = 1000  # Maximum steps per episode
    for t in range(MAX_T):
        actions = {agent: select_action(states[agent], agent).item() for agent in agents}
        observation, rewards, terminated, truncated, _ = env.step(actions)
        for agent in agents:
            temp_rewards[agent] += rewards[agent]
            reward = torch.tensor([rewards[agent]], device=device)
            done = terminated[agent] or truncated[agent]
            if done:
                next_state = None
            else:
                next_state = torch.tensor(observation[agent], dtype=torch.float32, device=device).unsqueeze(0)
            memories[agent].push(states[agent], torch.tensor([[actions[agent]]], device=device), next_state, reward)
            states[agent] = next_state
            optimize_model(agent)
            # Update target network
            target_net_state_dict = target_nets[agent].state_dict()
            policy_net_state_dict = policy_nets[agent].state_dict()
            for key in policy_net_state_dict:
                target_net_state_dict[key] = policy_net_state_dict[key]*TAU + target_net_state_dict[key]*(1-TAU)
            target_nets[agent].load_state_dict(target_net_state_dict)
        if all(terminated[agent] or truncated[agent] for agent in agents):
            break
    for agent in agents:
        reward_list[agent].append(temp_rewards[agent])
    #if i_episode % 10 == 0:
     #   plot_durations(reward_list)
end_time = time.time()
print('Complete')
print(f'Time elapsed is {end_time - start_time:.2f} seconds')
plot_durations(reward_list, show_result=True)
plt.ioff()
plt.show()

# Save the final plot
#plt.savefig('plot_durations.jpg', dpi=150)

#%%
import pickle
# Save the models and relevant data
for agent in agents:
    torch.save(policy_nets[agent].state_dict(), f'policy_net_{agent}.pth')
    torch.save(target_nets[agent].state_dict(), f'target_net_{agent}.pth')
    with open(f'memory_{agent}.pkl', 'wb') as f:
        pickle.dump(memories[agent], f)

# Save steps_done
with open('steps_done.pkl', 'wb') as f:
    pickle.dump(steps_done, f)

# Save reward list
with open('reward_list.pkl', 'wb') as f:
    pickle.dump(reward_list, f)

# Plotting the rewards at the end of training
for agent in agents:
    plt.figure()
    plt.plot(reward_list[agent], label=f"Agent {agent}")
    plt.xlabel('Episode')
    plt.ylabel('Reward')
    plt.title(f'Training Phase: Rewards - Agent {agent}')
    plt.legend()
    plt.savefig(f'Training_Phase_Agent_{agent}.jpg', dpi=150)
    plt.show()

# Execution Phase
def run_episode(env, policy_nets, agents, device):
    obs, info = env.reset()
    states = {agent: torch.tensor(obs[agent], dtype=torch.float32, device=device).unsqueeze(0) for agent in agents}
    episode_rewards = {agent: 0 for agent in agents}
    episode_actions = {agent: [] for agent in agents}
    MAX_T = 1000  # Maximum steps per episode
    for t in range(MAX_T):
        with torch.no_grad():
            actions = {agent: policy_nets[agent](states[agent]).max(1).indices.view(1, 1).item() for agent in agents}
        for agent in agents:
            episode_actions[agent].append(actions[agent])
        observation, rewards, terminated, truncated, _ = env.step(actions)
        for agent in agents:
            episode_rewards[agent] += rewards[agent]
            if terminated[agent] or truncated[agent]:
                states[agent] = None
            else:
                states[agent] = torch.tensor(observation[agent], dtype=torch.float32, device=device).unsqueeze(0)
        if all(terminated[agent] or truncated[agent] for agent in agents):
            break
    return episode_rewards, episode_actions

def run_random_policy(env, agents, device):
    obs, info = env.reset()
    states = {agent: torch.tensor(obs[agent], dtype=torch.float32, device=device).unsqueeze(0) for agent in agents}
    episode_rewards = {agent: 0 for agent in agents}
    episode_actions = {agent: [] for agent in agents}
    MAX_T = 1000  # Maximum steps per episode
    for t in range(MAX_T):
        actions = {agent: random.randint(0, len(env.blue_agents[agent].action_mapping) - 1) for agent in agents}
        for agent in agents:
            episode_actions[agent].append(actions[agent])
        observation, rewards, terminated, truncated, _ = env.step(actions)
        for agent in agents:
            episode_rewards[agent] += rewards[agent]
            if terminated[agent] or truncated[agent]:
                states[agent] = None
            else:
                states[agent] = torch.tensor(observation[agent], dtype=torch.float32, device=device).unsqueeze(0)
        if all(terminated[agent] or truncated[agent] for agent in agents):
            break
    return episode_rewards, episode_actions

# Running the execution phase
num_test_episodes = 20
trained_rewards = {agent: [] for agent in agents}
random_rewards = {agent: [] for agent in agents}

for episode in range(num_test_episodes):
    if episode % 10 == 0:
        print(f'Eval episode: {episode}')
    trained_episode_rewards, trained_episode_actions = run_episode(env, policy_nets, agents, device)
    random_episode_rewards, random_episode_actions = run_random_policy(env, agents, device)
    
    for agent in agents:
        trained_rewards[agent].append(trained_episode_rewards[agent])
        random_rewards[agent].append(random_episode_rewards[agent])
        print(f"Agent {agent} - Trained Actions: {trained_episode_actions[agent]}")
        print(f"Agent {agent} - Random Actions: {random_episode_actions[agent]}")

# Plotting the execution phase results
for agent in agents:
    plt.figure()
    plt.plot(trained_rewards[agent], label=f"Trained Agent {agent}")
    plt.plot(random_rewards[agent], label=f"Random Agent {agent}")
    plt.legend()
    plt.xlabel('Episode')
    plt.ylabel('Reward')
    plt.title(f'Execution Phase: Trained vs Random Policy - Agent {agent}')
    plt.savefig(f'Execution_Phase_Agent_{agent}.jpg', dpi=150)
    plt.show()
#%%
