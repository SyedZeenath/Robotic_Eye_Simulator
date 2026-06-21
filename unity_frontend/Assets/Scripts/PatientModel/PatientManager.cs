using UnityEngine;
using System;

public class PatientManager : MonoBehaviour
{
    public static PatientManager Instance;

    public Patient currentPatient;
    public PatientList patientList;

    public event Action<Patient> OnPatientChanged;

    void Awake()
    {
        Instance = this;
        LoadPatients();
    }
    void LoadPatients()
    {
        TextAsset json = Resources.Load<TextAsset>("patients");
        patientList = JsonUtility.FromJson<PatientList>(json.text);
        Debug.Log("Patients loaded: " + patientList.patients.Count);
    }

    public void SelectPatient(Patient patient)
    {
        currentPatient = patient;
        OnPatientChanged?.Invoke(patient);
        SimulationUI.Instance.SelectPatient(patient);
    }
}