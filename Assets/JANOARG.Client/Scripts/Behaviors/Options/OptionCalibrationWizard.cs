using System.Collections;
using JANOARG.Client.Behaviors.Common;
using JANOARG.Client.Behaviors.Options.Input_Types;
using JANOARG.Shared.Data.ChartInfo;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.EnhancedTouch;
using UnityEngine.UI;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

namespace JANOARG.Client.Behaviors.Options
{
    public class OptionCalibrationWizard : MonoBehaviour
    {
        public AudioSource CalibrationLoopPlayer;
        public AudioClip   CalibrationLoop;
        public float       CalibrationLoopBPM;

        [Space] public FloatOptionInput CurrentOptionInput;

        public float CurrentTime;
        public float SyncThreshold  = 0.03f;
        public int   TrialThreshold = 4;
        public float SyncOffset;

        [Space] public GameObject JudgmentOffsetHolder;

        public GameObject VisualOffsetHolder;

        [Space] public Image VisualOffsetLeft;

        public Image VisualOffsetRight;

        [Space] public GameObject InputHolder;

        public TMP_InputField InputField;
        public TMP_Text       InputFieldUnit;

        [Space] public Image Background;

        public  CanvasGroup FaderGroup;
        public  TMP_Text    InfoLabel;
        public  TMP_Text    JudgmentOffsetInstructionLabel;
        public  TMP_Text    VisualOffsetInstructionLabel;
        private float       _CumulativeOffset;

        private EaseEnumerator _CurrentAnim;

        private bool _IsActive;

        private double _SongStartDSP = 0.1f;
        
        private int    _LastTrialIndex;
        private int    _Samples;
        

        public void Start()
        {
            JudgmentOffsetHolder.SetActive(false);
            VisualOffsetHolder.SetActive(false);
            InputHolder.SetActive(false);
            gameObject.SetActive(false);
        }

        public void Update()
        {
            if (!_IsActive)
                return;

            double dspNow = AudioSettings.dspTime;

            // Absolute, drift-free timeline
            CurrentTime = (float)(dspNow - _SongStartDSP);

            if (CurrentOptionInput is AudioOffsetOptionInput)
            {
                float loopLength = 60f / CalibrationLoopBPM * 32f;

                // Use AUDIO as truth, not your timeline
                float audioTime =
                    (float)CalibrationLoopPlayer.timeSamples /
                    CalibrationLoopPlayer.clip.frequency;

                float loopTime = audioTime % loopLength;

                // Optional: keep debug, but DO NOT force resync every frame
                if (Mathf.Abs(loopTime - audioTime) > SyncThreshold)
                {
                    Debug.Log(loopTime + "\n" + audioTime);
                    // Intentionally no correction here
                }
            }
            else if (CurrentOptionInput is VisualOffsetOptionInput)
            {
                float trialDuration = 60f / CalibrationLoopBPM * 4f;

                float time = (CurrentTime / trialDuration) % 1f;

                float pos = Ease.Get(time, EaseFunction.Quartic, EaseMode.InOut) * 400f - 200f;
                VisualOffsetLeft.rectTransform.anchoredPosition = new Vector2(-pos, 0);
                VisualOffsetRight.rectTransform.anchoredPosition = new Vector2(pos, 0);

                float opacity = 1 - Ease.Get(Mathf.Abs(time * 2f - 1f), EaseFunction.Cubic, EaseMode.In);
                VisualOffsetLeft.color = VisualOffsetRight.color = new Color(1, 1, 1, opacity);
            }
        }


        public void IntializeWizard(FloatOptionInput optionInput)
        {
            gameObject.SetActive(true);
            InputHolder.SetActive(true);
            EnhancedTouchSupport.Enable();
            CurrentOptionInput = optionInput;
            CurrentTime = _CumulativeOffset = 0;
            _Samples = 0;
            _LastTrialIndex = -1;
            InfoLabel.gameObject.SetActive(false);

            if (optionInput is AudioOffsetOptionInput)
            {
                JudgmentOffsetHolder.SetActive(true);
                JudgmentOffsetInstructionLabel.gameObject.SetActive(true);
            }
            else if (optionInput is VisualOffsetOptionInput)
            {
                VisualOffsetHolder.SetActive(true);
                VisualOffsetInstructionLabel.gameObject.SetActive(true);
                VisualOffsetLeft.color = VisualOffsetRight.color = new Color(1, 1, 1, 0);
            }

            _CurrentAnim?.Skip();
            StartCoroutine(InitializeWizardAnim());
        }

        public IEnumerator InitializeWizardAnim()
        {
            _CurrentAnim = Ease.EnumAnimate(
                .45f, x =>
                {
                    float ease = Ease.Get(x * 1.5f, EaseFunction.Cubic, EaseMode.Out);
                    Background.color = new Color(0, 0, 0, .5f * ease);

                    Background.rectTransform.sizeDelta =
                        new Vector2(Background.rectTransform.sizeDelta.x, ease * 100);

                    float ease2 = Ease.Get(
                        x * 1.5f - .5f, EaseFunction.Cubic,
                        EaseMode.Out);

                    FaderGroup.alpha = ease2;
                });

            yield return _CurrentAnim;

            // --- DSP anchor setup ---
            double dspNow = AudioSettings.dspTime;
            double leadTime = 0.1; // safe scheduling margin

            _SongStartDSP = dspNow + leadTime;

            _IsActive = true;

            if (CurrentOptionInput is AudioOffsetOptionInput)
            {
                CalibrationLoopPlayer.clip = CalibrationLoop;

                // Schedule instead of immediate play
                CalibrationLoopPlayer.PlayScheduled(_SongStartDSP);
            }

            _CurrentAnim = null;
        }


        public void HideWizard()
        {
            _IsActive = false;
            EnhancedTouchSupport.Disable();
            InputField.onEndEdit.RemoveAllListeners();

            _CurrentAnim?.Skip();
            StartCoroutine(HideWizardAnim());
        }

        public IEnumerator HideWizardAnim()
        {
            CalibrationLoopPlayer.Pause();

            _CurrentAnim = Ease.EnumAnimate(
                .3f, x =>
                {
                    float ease = Ease.Get(x, EaseFunction.Cubic, EaseMode.Out);
                    Background.color = new Color(0, 0, 0, .5f * (1 - ease));

                    Background.rectTransform.sizeDelta =
                        new Vector2(
                            Background.rectTransform.sizeDelta.x,
                            100 * (1 - ease));

                    FaderGroup.alpha = 1 - ease;
                });
            yield return _CurrentAnim;

            _CurrentAnim = null;
            JudgmentOffsetHolder.SetActive(false);
            VisualOffsetHolder.SetActive(false);
            InputHolder.SetActive(false);
            gameObject.SetActive(false);
        }


        public void OnPanelPointerDown(BaseEventData eventData)
        {
            OnPanelPointerDown((PointerEventData)eventData);
        }

        public void OnPanelPointerDown(PointerEventData eventData)
        {
            if (!_IsActive) return;

            if (Touch.activeTouches.Count <= 0) return;

            Touch touch = Touch.activeTouches[0];

            float trialTime = (float)touch.startTime - Time.realtimeSinceStartup + CurrentTime;

            if (CurrentOptionInput is AudioOffsetOptionInput)
                trialTime += SyncOffset;
            else
                trialTime -= CommonSys.sMain.Preferences.Get("PLYR:JudgmentOffset", 0f) / 1000;

            float trialDuration = 60 / CalibrationLoopBPM * 4;
            int trialIndex = Mathf.FloorToInt(trialTime / trialDuration);
            Debug.Log(trialIndex + " " + trialTime + " " + trialDuration);

            if (_LastTrialIndex == trialIndex)
                return;

            _LastTrialIndex = trialIndex;

            float trialOffset = trialTime - (trialIndex + .5f) * trialDuration;
            _CumulativeOffset += trialOffset;
            _Samples++;

            if (_Samples < TrialThreshold)
            {
                InfoLabel.text = $"Press {TrialThreshold - _Samples} more times";
            }
            else
            {
                float averageOffset = -_CumulativeOffset / _Samples;
                averageOffset = Mathf.Round(averageOffset * 1000);
                InfoLabel.text = $"Average offset: {averageOffset:0}ms";
                InputField.text = averageOffset.ToString();
                InputField.onEndEdit.Invoke(InputField.text);
            }

            InfoLabel.gameObject.SetActive(true);

            TMP_Text targetLabel = CurrentOptionInput is AudioOffsetOptionInput
                ? JudgmentOffsetInstructionLabel
                : VisualOffsetInstructionLabel;

            InfoLabel.rectTransform.position = targetLabel.rectTransform.position;
            targetLabel.gameObject.SetActive(false);
        }

        public void AddOffset(float value)
        {
            InputField.text = (CurrentOptionInput.CurrentValue + value).ToString();
            InputField.onEndEdit.Invoke(InputField.text);
        }
    }
}