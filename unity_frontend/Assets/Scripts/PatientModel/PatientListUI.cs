using UnityEngine;
using TMPro;
using UnityEngine.UI;

public class PatientListUI : MonoBehaviour
{
    public Transform content;
    public GameObject patientItemPrefab;

    void Start()
    {
        PopulateList();
    }

    void PopulateList()
    {
        Debug.Log("PopulateList CALLED");
        // clear old items
        foreach (Transform child in content)
            Destroy(child.gameObject);

        foreach (var p in PatientManager.Instance.patientList.patients)
        {
            Debug.Log("Creating patient: " + p.name);
            GameObject obj = Instantiate(patientItemPrefab, content);
            obj.GetComponent<PatientItemUI>().Setup(p);
        }
    }
}