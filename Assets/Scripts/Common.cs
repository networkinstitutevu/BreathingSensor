using System;
using UnityEngine;

public class Common : MonoBehaviour
{
    //Flow

    //PLUX
    static bool usePlux = true;
    static string pluxMAC = "00:07:80:79:6F:D2"; //D1
    static bool pluxConnectionError = false;
    static bool startAcq = true; //From Main to LogPLUX to start data acquisition
    static bool startSensorLogging = false;
    static bool sensorRunning = false;

    //Vars
    static int ppn = 0;
    static string timeFormat = "dd-MM-yyy,HH:mm:ss:fff";

    //Breathing algorithm
    static float avgRSP;
    static bool avgRSPchanged = true; //TODO default = false
    static float absRRminimum = 1;
    static float absRRmaximum = 30;
    static float[] rspHistory = new float[4];
    static float baseLineRSP = 0;
    static float targetRSP = 0;
    static int maxRSP = 100;
    static int minRSP = 0;
    static double targetRRrange = 1.0; //Once the targetRR is reached this value is used to determine the "safe" range +/- around the targetRR before feedback changes again
    static bool targetRRoverride = false;
    static int indexTargetTable = 0; //Holds the index of the array holding the threshold tables. In FillFlowers.cs
    static bool clampedAVG = true;

    //Controller
    static bool buttonPressed = false;

    public static string sensorStatus { get; set; } //From LogPLUX to Main to show status of scanning and connecting to the plux
    public static string PluxMAC { get => pluxMAC; set => pluxMAC = value; }
    public static bool PluxConnectionError { get => pluxConnectionError; set => pluxConnectionError = value; }
    public static bool StartAcq { get => startAcq; set => startAcq = value; }
    public static bool StartSensorLogging { get => startSensorLogging; set => startSensorLogging = value; }
    public static bool UsePlux { get => usePlux; set => usePlux = value; }
    public static int Ppn { get => ppn; set => ppn = value; }
    public static float AvgRSP { get => avgRSP; set => avgRSP = value; }
    public static bool AvgRSPchanged { get => avgRSPchanged; set => avgRSPchanged = value; }
    public static float[] RspHistory { get => rspHistory; set => rspHistory = value; }
    public static float BaseLineRSP { get => baseLineRSP; set => baseLineRSP = value; }
    public static int MaxRSP { get => maxRSP; set => maxRSP = value; }
    public static int MinRSP { get => minRSP; set => minRSP = value; }
    public static string TimeFormat { get => timeFormat; set => timeFormat = value; }
    public static bool SensorRunning { get => sensorRunning; set => sensorRunning = value; }
    public static float TargetRSP { get => targetRSP; set => targetRSP = value; }
    public static float AbsRRminimum { get => absRRminimum; set => absRRminimum = value; }
    public static float AbsRRmaximum { get => absRRmaximum; set => absRRmaximum = value; }
    public static double TargetRRrange { get => targetRRrange; set => targetRRrange = value; }
    public static bool TargetRRoverride { get => targetRRoverride; set => targetRRoverride = value; }
    public static int IndexTargetTable { get => indexTargetTable; set => indexTargetTable = value; }
    public static bool ClampedAVG { get => clampedAVG; set => clampedAVG = value; }
    public static bool ButtonPressed { get => buttonPressed; set => buttonPressed = value; }

    static System.Random _r = new System.Random();
    public static int GetRND(int low, int high) //High is EXCLUSIVE!
    {
        return (int)_r.Next(low, high);
    }
}
