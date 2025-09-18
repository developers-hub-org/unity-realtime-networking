using System;
using System.Collections.Generic;
using UnityEngine;

namespace DevelopersHub.RealtimeNetworking
{
    public class Dispatcher : MonoBehaviour
    {

        private static readonly List<Action> _executeOnMainThread = new List<Action>();
        private static readonly List<Action> _executeCopiedOnMainThread = new List<Action>();
        private static bool _actionToExecuteOnMainThread = false;
        private static bool _initialized = false;
        private static Dispatcher _instance = null;  public static Dispatcher singleton { get { return _instance; } }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        public static void Initialize()
        {
            if (_initialized) { return; }
            _initialized = true;
            _instance = FindFirstObjectByType<Dispatcher>();
            if (_instance == null)
            {
                _instance = new GameObject("RealtimeNetworkingThreadDispatcher").AddComponent<Dispatcher>();
            }
            DontDestroyOnLoad(_instance.gameObject);
        }

        private void Awake()
        {
            Initialize();
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }

        private void Update()
        {
            if (_actionToExecuteOnMainThread)
            {
                _executeCopiedOnMainThread.Clear();
                lock (_executeOnMainThread)
                {
                    _executeCopiedOnMainThread.AddRange(_executeOnMainThread);
                    _executeOnMainThread.Clear();
                    _actionToExecuteOnMainThread = false;
                }
                for (int i = 0; i < _executeCopiedOnMainThread.Count; i++)
                {
                    _executeCopiedOnMainThread[i]();
                }
            }
        }

        public static void ExecuteOnMainThread(Action action)
        {
            if (_instance == null)
            {
                Debug.Log("Threading not initialized.");
                return;
            }
            if (action == null)
            {
                Debug.Log("No action to execute on main thread.");
                return;
            }
            lock (_executeOnMainThread)
            {
                _executeOnMainThread.Add(action);
                _actionToExecuteOnMainThread = true;
            }
        }

    }
}