using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class PatientItemUI : MonoBehaviour
{
    public TMP_Text nameText;
    public Button button;

    private Patient patient;

    public void Setup(Patient p)
    {
        patient = p;
        nameText.text = p.name;

        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(OnClick);
    }

    void OnClick()
    {
        PatientManager.Instance.SelectPatient(patient);
    }
}