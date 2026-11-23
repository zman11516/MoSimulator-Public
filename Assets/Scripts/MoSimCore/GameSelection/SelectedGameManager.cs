using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MoSimCore.GameSelection
{
    public class SelectedGameManager : MonoBehaviour
    {
        public static SelectedGameManager Instance { get; private set; }

        public static string PendingSelectedGameGuid { get; set; } = "";
        
        [field: SerializeField] public GameOption SelectedGame { get; set; }

        public List<GameOption> GameOptions { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            LoadGame();
        }

        private void Start()
        {
            InitializeSelectedGame();
        }
        
        private void LoadGame()
        {
            GameOptions = Resources.LoadAll<GameOption>("GameOptions").ToList();

            GameOptions.Sort((a, b) => b.GameYear.CompareTo(a.GameYear));
        }

        public void SetSelectedGameFromGuid(string selectedGameGuid)
        {
            if (string.IsNullOrEmpty(selectedGameGuid))
            {
                return;
            }

            var match = GameOptions.FirstOrDefault(option => option.GetGuid() == selectedGameGuid);
            if (match != null)
            {
                SelectedGame = match;
            }
        }

        public bool TrySetSelectedGameFallback(string selectedGameGuid)
        {
            SetSelectedGameFromGuid(selectedGameGuid);
            if (SelectedGame != null)
            {
                return true;
            }

            if (GameOptions.Count > 0)
            {
                SelectedGame = GameOptions[0];
                return true;
            }

            return false;
        }

        private void InitializeSelectedGame()
        {
            if (SelectedGame != null)
            {
                return;
            }

            SetSelectedGameFromGuid(PendingSelectedGameGuid);
            if (SelectedGame != null)
            {
                return;
            }

            var activeSceneName = SceneManager.GetActiveScene().name;
            foreach (var gameOption in GameOptions)
            {
                if (SafeSceneName(gameOption.GameScene) == activeSceneName ||
                    SafeSceneName(gameOption.MainMenuScene) == activeSceneName)
                {
                    SelectedGame = gameOption;
                }
            }

            if (SelectedGame == null && GameOptions.Count > 0)
            {
                SelectedGame = GameOptions[0];
            }
        }

        // A game option can reference scenes that are not in this checkout (e.g. menu scenes left out of the public
        // project); reading such a reference's name throws, so treat it as "no scene" instead.
        private static string SafeSceneName(Eflatun.SceneReference.SceneReference scene)
        {
            try
            {
                return scene != null && scene.UnsafeReason == Eflatun.SceneReference.SceneReferenceUnsafeReason.None
                    ? scene.Name
                    : null;
            }
            catch (Eflatun.SceneReference.Exceptions.SceneReferenceException)
            {
                return null;
            }
        }
    }
}