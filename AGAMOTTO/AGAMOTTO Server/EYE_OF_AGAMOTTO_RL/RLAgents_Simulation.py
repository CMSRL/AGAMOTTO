import gymnasium as gym
import numpy as np
import ale_py
import torch
import matplotlib.pyplot as plt
import torch.nn as nn
import torch.optim as optim
import torch.nn.functional as F
import math
import random
import matplotlib
from collections import namedtuple, deque
from itertools import count
import time
from tqdm import tqdm

is_ipython = 'inline' in matplotlib.get_backend()
if is_ipython:
    from IPython import display

from EYE_OF_AGAMOTTO_RL.Agents import CommandAgent

# Base random seed for reproducibility
BASE_RANDOM_SEED = 58922320

#env = gym.make("ALE/Adventure-v5")
# Plotting function
def plot_durations(reward_list, show_result=False):
    plt.figure(1)
    plt.clf()


    if show_result:
        plt.title('Result')
    else:
        plt.title('Training...')

    plt.xlabel('Episode')
    plt.ylabel('Reward')
    plt.plot(reward_list, label=f"Agent")

    plt.legend()
    plt.pause(0.001)
    if is_ipython:
        if not show_result:
            display.display(plt.gcf())
            display.clear_output(wait=True)
        else:
            display.display(plt.gcf())

# normal reset, this changes the colour scheme by default
#obs, _ = env.reset()

def train_q_learning(
    env,
    use_action_mask: bool = True,
    episodes: int = 5000,
    seed: int = BASE_RANDOM_SEED,
) -> dict:

    commandAgent = CommandAgent(env)
    # Track episode rewards for analysis
    episode_rewards = []

    for episode in range(episodes):
        # Reset environment
        state, info = env.reset(seed=seed + episode)
        total_reward = 0
        max_t = 1000  # Maximum steps per episode
        for t in range(max_t):
            # Take action and observe result
            action = commandAgent.select_action(state)
            next_state, reward, done, truncated, info = env.step(action)
            total_reward += reward

            commandAgent.optimize_model()

            episode_rewards.append(total_reward)

            if done or truncated:
                break
    print(episode_rewards)
    return {
        "episode_rewards": episode_rewards,
        "mean_reward": np.mean(episode_rewards),
        "std_reward": np.std(episode_rewards),
    }

# Experiment parameters
n_runs = 12


device = torch.device(
    "cuda" if torch.cuda.is_available() else
    "mps" if torch.backends.mps.is_available() else
    "cpu"
)

if device.type == "cuda": episodes = 5000
else: episodes = 5

# Generate different seeds for each run
seeds = [BASE_RANDOM_SEED + i for i in range(n_runs)]

# Store results for comparison
masked_results_list = []
unmasked_results_list = []

# Run experiments with different seeds
for i, seed in enumerate(seeds):
    print(f"Run {i + 1}/{n_runs} with seed {seed}")

    # Train agent WITHOUT action masking
    env_unmasked = gym.make("ALE/Adventure-v5")
    unmasked_results = train_q_learning(
        env_unmasked,
        use_action_mask=False,
        seed=seed,
        episodes=episodes,
    )
    env_unmasked.close()
    unmasked_results_list.append(unmasked_results)



