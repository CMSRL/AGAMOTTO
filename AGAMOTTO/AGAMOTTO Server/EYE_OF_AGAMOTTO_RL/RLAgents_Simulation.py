import gymnasium as gym
import numpy as np

from EYE_OF_AGAMOTTO_RL.Agents import CommandAgent


# Base random seed for reproducibility
BASE_RANDOM_SEED = 58922320

env = gym.make("CarRacing-v3", render_mode="rgb_array", lap_complete_percent=0.95, domain_randomize=False, continuous=False)

# normal reset, this changes the colour scheme by default
obs, _ = env.reset()

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
            action = commandAgent.select_action(state, use_action_mask=use_action_mask)
            next_state, reward, done, truncated, info = env.step(action)
            total_reward += reward

            commandAgent.optimize_model()

            episode_rewards.append(total_reward)

            if done or truncated:
                break

    return {
        "episode_rewards": episode_rewards,
        "mean_reward": np.mean(episode_rewards),
        "std_reward": np.std(episode_rewards),
    }

# Experiment parameters
n_runs = 12
episodes = 5000


# Generate different seeds for each run
seeds = [BASE_RANDOM_SEED + i for i in range(n_runs)]

# Store results for comparison
masked_results_list = []
unmasked_results_list = []

# Run experiments with different seeds
for i, seed in enumerate(seeds):
    print(f"Run {i + 1}/{n_runs} with seed {seed}")

    # Train agent WITH action masking
    env_masked = gym.make("Taxi-v3")
    masked_results = train_q_learning(
        env_masked,
        use_action_mask=True,
        seed=seed,
        episodes=episodes,
    )
    env_masked.close()
    masked_results_list.append(masked_results)

    # Train agent WITHOUT action masking
    env_unmasked = gym.make("Taxi-v3")
    unmasked_results = train_q_learning(
        env_unmasked,
        use_action_mask=False,
        seed=seed,
        episodes=episodes,
    )
    env_unmasked.close()
    unmasked_results_list.append(unmasked_results)

env.close()

