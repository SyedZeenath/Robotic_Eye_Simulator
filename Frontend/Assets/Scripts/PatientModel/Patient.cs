[System.Serializable]
public class Patient
{
    public string name;
    public string bppvType;   // "posterior", "horizontal", "anterior"
    // BPPV diagnosis info from JSON
    public string bppvSide;   // "right" or "left"
    public int age;
    public float severity;
    public string neckStiffness;

}