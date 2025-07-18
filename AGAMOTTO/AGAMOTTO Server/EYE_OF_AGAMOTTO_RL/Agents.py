import numpy as np
import random
from collections import namedtuple, deque
import torch
import torch.nn as nn
import torch.optim as optim
import torch.nn.functional as F

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

class PlayerAgent:
    def __init__(self, agent_id, network_zone, team = ""):
        self.agent_id = agent_id
        self.team = team #can be "Friend" or "Enemy"

    def get_id(self):
        return self.agent_id

class CommandAgent:
    def __init__(
        self,
        env
    ):
        self.env = env
        self.BATCH_SIZE=128
        self.GAMMA = 0.99
        self.EPS_START = 0.9
        self.EPS_END = 0.05
        self.EPS_DECAY = 1000
        self.TAU = 0.005
        self.LR = 1e-4
        self.epsilon = 0.1

        self.device = torch.device(
            "cuda" if torch.cuda.is_available() else
            "mps" if torch.backends.mps.is_available() else
            "cpu"
        )

        #self.action_mapping = self._create_action_mapping(),
        obs, _ = self.env.reset()
        # Initialize networks, optimizers, and memories for each agent
        self.POLICY_NET = DQN(len(obs), self.env.action_space.n).to(self.device)
        self.TARGET_NET = DQN(len(obs), self.env.action_space.n).to(self.device)
        self.OPTIMIZER = optim.AdamW(self.POLICY_NET.parameters(), lr=self.LR, amsgrad=True)
        self.MEMORIES = ReplayMemory(10000)

        # Copy policy network parameters to target networks
        self.TARGET_NET.load_state_dict(self.POLICY_NET.state_dict())



    def _create_action_mapping(self):
        actions = self.action_space()
        action_mapping = {i: action for i, action in enumerate(actions)}
        return action_mapping

    def action_space(self):
        actions = self.env.action_space
        ##UPDATEEEEEEE
        return actions

    # Define action selection function
    def select_action(self, state):

        # Epsilon-greedy action selection with masking
        if np.random.random() < self.epsilon:
            # Random action selection
                # Select from all actions
                action = np.random.randint(0, self.env.action_space.n)
        else:
            # Greedy action selection
                # Consider all actions
                action = np.random.randint(0, self.env.action_space.n)
                #action = self.POLICY_NET(torch.from_numpy(state).max(1).indices.view(1, ))
        return action

    def optimize_model(self):
        if len(self.MEMORIES) < self.BATCH_SIZE:
            return
        transitions = self.MEMORIES.sample(self.BATCH_SIZE)
        batch = Transition(*zip(*transitions))

        # Mask to filter out final states
        non_final_mask = torch.tensor(tuple(map(lambda s: s is not None,
                                                batch.next_state)), device=self.device, dtype=torch.bool)
        # Concatenate the non-final next states
        non_final_next_states = torch.cat([s for s in batch.next_state if s is not None])
        state_batch = torch.cat(batch.state)
        action_batch = torch.cat(batch.action)
        reward_batch = torch.cat(batch.reward)

        # Compute Q(s_t, a)
        state_action_values = self.POLICY_NET(state_batch).gather(1, action_batch)

        # Compute V(s_{t+1})
        next_state_values = torch.zeros(self.BATCH_SIZE, device=self.device)
        with torch.no_grad():
            next_state_values[non_final_mask] = self.TARGET_NET(non_final_next_states).max(1).values
        # Compute expected Q values
        expected_state_action_values = (next_state_values * self.GAMMA) + reward_batch

        # Compute loss
        criterion = nn.SmoothL1Loss()
        loss = criterion(state_action_values, expected_state_action_values.unsqueeze(1))

        # Optimize the model
        self.OPTIMIZER.zero_grad()
        loss.backward()
        torch.nn.utils.clip_grad_value_(self.POLICY_NET.parameters(), 100)
        self.OPTIMIZER.step()
        return loss.item()

class BlueAgent(PlayerAgent):
    def __init__(self, agent_id, team = ""):
        super().__init__(agent_id, team)

class RedAgent(PlayerAgent):
    def __init__(self, agent_id, team = ""):
        super().__init__(agent_id, team)
