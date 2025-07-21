import numpy as np
import random
import cv2
from collections import namedtuple, deque
import torch
import torch.nn as nn
import torch.optim as optim
import gymnasium as gym
from EYE_OF_AGAMOTTO_RL.DQN import ConvDQN
from EYE_OF_AGAMOTTO_RL.DQN import ReplayBuffer
from tqdm import tqdm



# Preprocessing function
def preprocess(obs):
    obs = cv2.cvtColor(obs, cv2.COLOR_RGB2GRAY)
    obs = cv2.resize(obs, (84, 84))
    return obs / 255.0

    # Epsilon-greedy action selection

# Epsilon-greedy action selection
def select_action(state, policy_net, epsilon, action_space, device):
    if random.random() < epsilon:
        return random.randrange(action_space)
    with torch.no_grad():
        state = torch.tensor(state, dtype=torch.float32).unsqueeze(0).to(device)
        return policy_net(state).argmax().item()


class PlayerAgent:
    def __init__(self, agent_id, network_zone, team = ""):
        self.agent_id = agent_id
        self.team = team #can be "Friend" or "Enemy"

    def get_id(self):
        return self.agent_id

class CommandAgent:
    def __init__(
        self
    ):

        self.GAMMA = 0.99
        self.LR = 1e-4
        self.BATCH_SIZE = 32
        self.MEMORY_SIZE = 100_000
        self.EPS_START = 1.0
        self.EPS_END = 0.1
        self.EPS_DECAY = 1_000_000
        self.TARGET_UPDATE = 10_000
        self.DEVICE = torch.device("cuda" if torch.cuda.is_available() else "cpu")

    # Main training loop
    def train(self, game_type, num_episodes):
        env = gym.make(game_type, render_mode="human", frameskip=4)
        action_space = env.action_space.n

        policy_net = ConvDQN(action_space).to(self.DEVICE)
        target_net = ConvDQN(action_space).to(self.DEVICE)
        target_net.load_state_dict(policy_net.state_dict())
        target_net.eval()

        optimizer = optim.Adam(policy_net.parameters(), lr=self.LR)
        memory = ReplayBuffer(self.MEMORY_SIZE)

        epsilon = self.EPS_START
        steps_done = 0
        state_stack = deque(maxlen=4)

        for episode in tqdm(range(1, num_episodes+1)):
            obs, _ = env.reset()
            frame = preprocess(obs)
            state_stack.clear()
            state_stack.extend([frame] * 4)
            total_reward = 0

            while True:
                state = np.stack(state_stack, axis=0)
                action = select_action(state, policy_net, epsilon, action_space, self.DEVICE)
                next_obs, reward, terminated, truncated, _ = env.step(action)
                done = terminated or truncated
                next_frame = preprocess(next_obs)
                state_stack.append(next_frame)
                next_state = np.stack(state_stack, axis=0)

                memory.push((state, action, reward, next_state, done))
                total_reward += reward

                if len(memory) >= self.BATCH_SIZE:
                    states, actions, rewards, next_states, dones = memory.sample(self.BATCH_SIZE)

                    states = torch.tensor(states, dtype=torch.float32).to(self.DEVICE)
                    actions = torch.tensor(actions, dtype=torch.long).unsqueeze(1).to(self.DEVICE)
                    rewards = torch.tensor(rewards, dtype=torch.float32).unsqueeze(1).to(self.DEVICE)
                    next_states = torch.tensor(next_states, dtype=torch.float32).to(self.DEVICE)
                    dones = torch.tensor(dones, dtype=torch.float32).unsqueeze(1).to(self.DEVICE)

                    q_values = policy_net(states).gather(1, actions)
                    next_q_values = target_net(next_states).max(1)[0].detach().unsqueeze(1)
                    expected_q_values = rewards + self.GAMMA * next_q_values * (1 - dones)

                    loss = nn.MSELoss()(q_values, expected_q_values)
                    optimizer.zero_grad()
                    loss.backward()
                    optimizer.step()

                    if steps_done % self.TARGET_UPDATE == 0:
                        target_net.load_state_dict(policy_net.state_dict())

                steps_done += 1
                epsilon = max(self.EPS_END, self.EPS_START - steps_done / self.EPS_DECAY)

                if done:
                    print(f"Episode {episode}, Total Reward: {total_reward}, Epsilon: {epsilon:.3f}")
                    break

        env.close()


class BlueAgent(PlayerAgent):
    def __init__(self, agent_id, team = ""):
        super().__init__(agent_id, team)

class RedAgent(PlayerAgent):
    def __init__(self, agent_id, team = ""):
        super().__init__(agent_id, team)
