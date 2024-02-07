using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// This can use live PLUX data or read from a raw data file created by this script using the PLUX 
/// 
/// Handles connection and data processing from the PLUX Piezo Respiration strap
/// Logs to a raw (10Hz) file and a processed file (in breaths-per-minute)
/// 
/// The algorithm does:
/// Get first 20 data points (lag/WindowSize)
/// Shift dataPoints array one, to make place for a new value at the end of the array
/// Add the new data point using the <influence> and the previous stored data point
/// Average the updated data points array to a new average value and store this in the last position of the avgFilter array
/// Then see if we have a peak (high value then going down) or valley (low value then going up) and store the time tick (1 tick = 0.1s)
/// If there are at least 2 peaks and valleys detected (each) then calculate the peak-to-peak and valley-to-valley time and the breaths-per-minute for both
/// Then average these, store these averages, those will be again averaged (per 3) and stored in the RSPavg
/// If the RSPavg change is higher than maxDeltaRSPavg then the change in the average value will be clamped to that max
/// 
/// </summary>
public class RespiratorySensorZ : MonoBehaviour
{
    #region Variables
    //UI
    [SerializeField] TMP_Text txtMessage;
    [SerializeField] TMP_Text txtStatus;
    [SerializeField] TMP_InputField inpPPN;
    [SerializeField] GameObject pnlPPN;
    Keyboard kb;

    //Logging
    StreamWriter theRAWFile; //new raw data file
    StreamWriter processedFile;
    StreamWriter averageFile;
    StreamReader savedRAWfile; //previously saved raw data file
    bool fileRead = false; //Done reading the raw data file?
    bool writeMarkerRaw, writeMarkerAverage;

    //Flow
    DateTime startTime;
    int step = 0; //Used in update to control the flow

    //PLUX
    private delegate bool FPtr(int nSeq, IntPtr dataIn, int dataInSize);
    PluxDeviceManager PluxDevManager;
    List<int> ActiveChannels = new List<int>() { 1, 0, 0, 0 }; //Set up channels if not 0 then that channel is active;
    List<string> ListDevices;
    int SamplingRate = 10; //10Hz!
    int Resolution = 8; //8bit
    string SelectedDevice = "";

    //Respiration vars
    int windowSize = 20; //number of data points (history) for smoothing (mean), each datapoint takes 0.1s (10Hz) => 2.0s
    bool up = false; //Flag to keep track of looking for peaks or valleys
    bool switched = false; //from up to down or vv, used to process a new bpm value
    List<float> dataPoints = new List<float>(); //Current set of raw data points

    int[] peak = new int[2] { 0, 0 }; //time tick in avgSignal of the previous 2 peaks
    int[] valley = new int[2] { 0, 0 }; //time tick in avgSignal of the previous 2 valleys
    float RRpeaks, RRvalleys, RRaverage; //peak-peak and valley-valley and average breaths-per-minute values calculated after each new peak and valley
    float[] previousRTA; //History of previous Respiration Rate Average (average of peak period and valley period)
    [SerializeField] int smoothingSize = 3; //Number of rta values to smooth for RSPavg
    float averageRR, prevRRpeak, prevRRvalley; //running average of rta
    float prevRRaverage;
    float maxDeltaRR = 1.5f; //Max difference between RSPavg and prevRSPavg else clamp it to avoid too much effect of an outlier
    float startRSP = 14; //Should be reasonble average, but signal acquisition is too slow for this value to have any value :-)
    //

    //Z scores
    float influence = 0.1f; //Weight of the latest data point to the set. Blend between the new value (n) and the previous (n-1)
    float[] avgFilter;
    bool skipFirst = true; //Skip the first peak/valley as that is an artefact of the algorithm
    float[] stdFilter;
    float threshold = 6f;
    int[] signal = new int[2] { 0, 0 };
    bool processing = false;
    bool detected = false;
    //
    #endregion
    void Start()
    {
        ReadSettingsFile(); //First read the external settings
        avgFilter = new float[windowSize]; //Define the average array
        stdFilter = new float[windowSize];

        kb = InputSystem.GetDevice<Keyboard>(); //Setup keyboard

        writeMarkerRaw = writeMarkerAverage = false; //init

        Common.BaseLineRSP = startRSP; //Make sure we start all RR values with a reasonable value
        averageRR = startRSP;

        prevRRpeak = startRSP;
        prevRRvalley = startRSP;
        prevRRaverage = startRSP;

        //This is for smoothing the RTA values
        previousRTA = new float[smoothingSize]; //Init to the number of rtas we use for smoothing
        for (int i = 0; i < previousRTA.Length; i++) //Prefill the history. Looks like the slow start of valid signal values makes this superfluous
        {
            previousRTA[i] = Common.BaseLineRSP;
        }

        SetupLogFileFolders();//Check Logfiles folder is not present create
        if (Common.UsePlux) //Use PLUX live data or read from a raw data file?
        {
            UnityEngine.Debug.Log("Starting PLUX");
            txtStatus.text = "Starting sensor...";
            PluxDevManager = new PluxDeviceManager(ScanResults, ConnectionDone, AcquisitionStarted, OnDataReceived, OnEventDetected, OnExceptionRaised);
            UnityEngine.Debug.Log("Welcome Number: " + PluxDevManager.WelcomeFunctionUnity());//Tests if the init worked
            Common.sensorStatus = "Scanning devices...";
            Common.StartAcq = true;
            Scan();//First scan for devices so that the Device Manager knows there are devices! Callback = ScanResults
        }
        else //Read file
        {
            Common.SensorRunning = true;
            step = 1000;
        }
    }
    private void Update()
    {
        if (Common.ButtonPressed)
        {
            Common.ButtonPressed = false;
            writeMarkerRaw = writeMarkerAverage = true; //Write marker in log files
        }
        switch (step)
        {
            case 0: //Wait for PLUX Scan if succesful goes to case 100
                break;
            case 100: //PLUX Scan done
                Debug.Log("Trying to connect to sensor...");
                txtStatus.text = "Connecting to sensor & waiting for PPN...";
                Connect(); //Try connecting to our specific PLUX hub. Callback = ConnectionDone - goes to case 300
                step = 200;
                break;
            case 200: //Wait for connect, if the connection is succesful the callback will set step=300
                if (Common.PluxConnectionError)
                {
                    Debug.Log("There was an error connecting to the Plux...");
                    txtStatus.text = "Error connecting to the sensor...";
                    step = 999; //Quit
                }
                break;
            case 300: //Start data acquisition. Common.StartAqc <default = true> can be used elsewhere to start acquisition at a managed moment
                if (Common.StartAcq && Common.Ppn > 0) //Make sure we should start collecting data AND we have a valid PPN for the log file's names
                {
                    StartAcquisition(); //Start acquisition. Callback = AcquisitionStarted
                    Debug.Log("Data acquisition is starting.");
                    txtStatus.text = "Data acquisition started.";
                    step = 400;
                }
                break;
            case 400: //Create logs files
                if (Common.Ppn > 0)
                {
                    CreateLogs(); //Create log file for raw PLUX data
                    Debug.Log("Log files created.");
                    Debug.Log("Sensor connected. Waiting for valid signal...");
                    txtStatus.text = "Log files created. Waiting for valid signal...";
                    step = 500;
                }
                break;
            case 500: //On Sensor Panel show data from PLUX
                if (Common.SensorRunning) //Acqusition is now running. Set in acquisitiondone
                {
                    Common.StartSensorLogging = true; //Start logging
                    Debug.Log("sensorStatus is RUN - Logging started");
                    txtStatus.text = "Session running...";
                    step = 700;
                }
                break;
            case 700: //Main loop. Wait for end
                if(Common.AvgRSPchanged)
                {
                    Common.AvgRSPchanged = false;
                    txtMessage.text = Common.AvgRSP.ToString("0.00");
                }
                if(kb.qKey.wasPressedThisFrame) //check Q-key for quit
                {
                    Debug.Log("QUIT");
                    nowQuit();
                }
                break;
            case 999: //Problem with the plux - ABORT
                Debug.Log("Problem with PLUX");
                break;
                ///
                ///
            case 1000: //Process from a RAW data file
                SetDataFiles();
                Common.StartSensorLogging = true;
                step = 1100;
                break;
            case 1100: //
                if (!fileRead)
                {
                    OnRawFileRead(); //Read the file
                }
                else
                {
                    step = 1200;
                }
                break;
            case 1200:
                nowQuit();
                break;
        }
    }
    //This is where the PLUX data is handled!
    public void OnDataReceived(int tickNow, int[] data)//, int dataLength)
    {
        string dataString = data[0].ToString();
        if (Common.StartSensorLogging)
        {
            string toadd = "";
            if (writeMarkerRaw)
            {
                writeMarkerRaw = false; //reset
                toadd = ",*";
            }
            theRAWFile.WriteLine(DateTime.Now.ToString(Common.TimeFormat) + "," + tickNow + "," + dataString + toadd);
        }
        if (dataPoints.Count == windowSize) //Do we have enough points? Only at start NOT
        {
            GetRSP(tickNow, data[0]); //Calculate live respiratory rate, just 1 value from PLUX
        }
        else //Initial filling of the datapoints list
        {
            dataPoints.Add(data[0]); //Fill until count = windowSize
        }
    }
    void OnRawFileRead()
    {
        string line = "";
        if ((line = savedRAWfile.ReadLine()) != null)
        {
            int tick = Convert.ToInt32(line.Split(',')[2]);
            int dataValue = Convert.ToInt32(line.Split(',')[3]); //Get the value
            if (dataPoints.Count == windowSize) //Do we have enough points? Only at start NOT
            {
                GetRSP(tick, dataValue); //Calculate live respiratory rate, just 1 value from PLUX
            }
            else //Initial filling of the datapoints list
            {
                dataPoints.Add(dataValue); //Fill until count = windowSize
            }
        }
        else
        {
            fileRead = true;
        }
    }
    void GetRSP(int tickNow, float data)
    {
        GetRR(tickNow, data); //Using weighted average and peak-valley detection
        //GetRRzx(tickNow, data); //Using weighted average and zero-crossing detection - slightly more wobbly, slightly higher RR
    }
    void GetRR(int tickNow, float data) //Calculate RR using weighted averages and peak-valley detection
    {
        //tickNow is time tick which is same as frequency which is 0.1s per tick
        for (int j = 1; j < windowSize; j++) { dataPoints[j - 1] = dataPoints[j]; } //Slide array one up
        dataPoints[windowSize - 1] = (influence * data) + ((1 - influence) * dataPoints[windowSize - 1]); //Add new data point with influence
        for (int j = 1; j < (windowSize); j++) { avgFilter[j - 1] = avgFilter[j]; } //Slide array one up
        avgFilter[(windowSize) - 1] = (float)dataPoints.Average(); //Add new average point
        string toadd = "";
        if (writeMarkerAverage)
        {
            writeMarkerAverage = false; //reset
            toadd = ",*";
            //Need to insert extra line in processed as this is only updated when a new half period is detected
            processedFile.WriteLine(DateTime.Now.ToString(Common.TimeFormat) + "," + (tickNow - 1) + "," + RRaverage.ToString("0.0") + "," + averageRR.ToString("0.0") + toadd);//Write values to file
        }
        averageFile.WriteLine(DateTime.Now.ToString(Common.TimeFormat) + "," + tickNow + "," + avgFilter[avgFilter.Length - 1] + toadd); //Write average to file
        if (avgFilter[windowSize - 2] > 1) //need at least two averages
        {
            if (avgFilter[avgFilter.Length - 1] > avgFilter[avgFilter.Length - 2]) //Is the new avg [-1] larger than the previous [-2]? => going UP
            {
                if (!up && skipFirst) //skipFirst is to ignore the first valley or peak
                {
                    skipFirst = false; //Skipped the first valley
                    up = true; //Look for peaks
                }
                else
                {
                    if (!up) //Valley detection: We were going down, now up => we should have a valley
                    {
                        up = true; //Switch to looking for peaks
                        valley[0] = valley[1]; //shift
                        valley[1] = tickNow - 1; //Set time of previous data point as that is the actual valley
                        switched = true; //Process
                    }
                }
            }
            else //Peak detection: going DOWN
            {
                if (up && skipFirst)
                {
                    skipFirst = false;
                    up = false;
                }
                else
                {
                    if (up) //We were going up, now down => we should have peak
                    {
                        up = false; //Switch to look for valleys
                        peak[0] = peak[1]; //shift
                        peak[1] = tickNow - 1; //Save new time position
                        switched = true; //process
                    }
                }
            } 
            if (switched) //Did we just find a peak or valley?
            {
                switched = false;
                if (peak[0] != 0) { RRpeaks = 60f / ((1f / (float)SamplingRate) * (float)(peak[1] - peak[0])); } //If there are 2 peaks, calculate the breaths-per-minute
                if (valley[0] != 0) { RRvalleys = 60f / ((1f / (float)SamplingRate) * (float)(valley[1] - valley[0])); } //If there are 2 valleys, calculate the breaths-per-minute
                if (RRpeaks != 0 && RRvalleys != 0) //If we have valid numbers
                {
                    if (Common.ClampedAVG && Math.Abs(RRpeaks - prevRRpeak) > maxDeltaRR) //Clamp maximum RR change to <maxDeltaRR> (1.5)
                    {
                        if (RRpeaks > prevRRpeak) { RRpeaks = prevRRpeak + maxDeltaRR; }
                        else { RRpeaks = prevRRpeak - maxDeltaRR; }
                    }
                    if (Common.ClampedAVG && Math.Abs(RRvalleys - prevRRvalley) > maxDeltaRR) //Clamp maximum RR change to <maxDeltaRR> (1.5)
                    {
                        if (RRvalleys > prevRRvalley) { RRvalleys = prevRRvalley + maxDeltaRR; }
                        else { RRvalleys = prevRRvalley - maxDeltaRR; }
                    }

                    if (RRpeaks < Common.AbsRRminimum) { RRpeaks = prevRRpeak; } //Clamp RR between min-max allowed values (4-25)
                    if (RRpeaks > Common.AbsRRmaximum) { RRpeaks = prevRRpeak; }
                    prevRRpeak = RRpeaks;
                    if (RRvalleys < Common.AbsRRminimum) { RRvalleys = prevRRpeak; }
                    if (RRvalleys > Common.AbsRRmaximum) { RRvalleys = prevRRpeak; }
                    prevRRvalley = RRvalleys;

                    RRaverage = (RRpeaks + RRvalleys) / 2f; //Get average RR of peaks and valleys

                    //Now create a smoothed value of the Breaths-Per-Minute and limit the effect of outliers
                    float sum = 0; //Init
                    for (int i = 1; i < smoothingSize; i++) //Run through the saved RTA values
                    {
                        previousRTA[i - 1] = previousRTA[i]; //Move them up
                        sum += previousRTA[i - 1];// * weights[i - 1]) ; //Now add them multiplying by the weight for that value - newer is more influence by default
                    }
                    previousRTA[previousRTA.Length - 1] = RRaverage; //Save new RTA value
                    sum += RRaverage;// * weights[weights.Length - 1]; //Add this value to the sum multiplied by the weight
                    averageRR = sum / previousRTA.Length; //Get average RRaverage over smoothingSize weighted RRaverage

                    Common.AvgRSP = averageRR; //So that other scripts can see it too
                    Common.AvgRSPchanged = true; //Let other scripts know the AvgRSP has changed
                    if (Common.StartSensorLogging) //If logging
                    {
                        processedFile.WriteLine(DateTime.Now.ToString(Common.TimeFormat)+ "," + (tickNow - 1) + "," + RRaverage.ToString("0.0") + "," + averageRR.ToString("0.0"));//Write values to file
                    }
                }
            }
        }
    }
    void GetRRzx(int tickNow, float data) //Calculate RR using weighted averages and zero-crossing detection
    {
        //tickNow is time tick which is same as frequency which is 0.1s per tick
        for (int j = 1; j < windowSize; j++) { dataPoints[j - 1] = dataPoints[j]; } //Slide array one up
        dataPoints[windowSize - 1] = (influence * data) + ((1 - influence) * dataPoints[windowSize - 1]); //Add new data point with influence
        for (int j = 1; j < (windowSize); j++) { avgFilter[j - 1] = avgFilter[j]; } //Slide array one up
        avgFilter[(windowSize) - 1] = (float)dataPoints.Average(); //Add new average point
        averageFile.WriteLine(DateTime.Now.ToString(Common.TimeFormat) + "," + tickNow + "," + avgFilter[avgFilter.Length - 1]); //Write average to file

        for (int j = 1; j < windowSize; j++) { stdFilter[j - 1] = stdFilter[j]; } //move array one down
        stdFilter[windowSize - 1] = GetStd(avgFilter);//std

        if (avgFilter[windowSize - 2] > 1) //need at least two averages
        {
            //below is the STD to rate-of-chage in signal (zero-crossing) detection
            if (Math.Abs(data - avgFilter[windowSize - 2]) > threshold * stdFilter[windowSize - 2])
            {
                if (!detected)
                {
                    signal[0] = signal[1];
                    signal[1] = tickNow;
                    processing = true;
                    detected = true;
                }
            }
            else
            {
                detected = false;
            }
            if(processing)
            {
                processing = false;
                float currentRR = 0;
                if (signal[0] != 0)
                {
                    currentRR = 60f / (2f * ((1f / (float)SamplingRate) * (float)(signal[1] - signal[0])));
                    if (currentRR != 0)
                    {
                        if (Common.ClampedAVG && Math.Abs(currentRR - prevRRpeak) > maxDeltaRR) //If the new RSPavg jumps too much, clamp it
                        {
                            if (currentRR > prevRRpeak) { currentRR = prevRRpeak + maxDeltaRR; } //Either add or substract the delta limit
                            else { currentRR = prevRRpeak - maxDeltaRR; }
                        }

                        if (currentRR < Common.AbsRRminimum) { currentRR = prevRRpeak; } //If invalid, use previous
                        if (currentRR > Common.AbsRRmaximum) { currentRR = prevRRpeak; } //If invalid, use previous
                        prevRRpeak = currentRR; //Remember this value for next time

                        float sum = 0; //Init
                        for (int i = 1; i < smoothingSize; i++)
                        {
                            previousRTA[i - 1] = previousRTA[i];
                            sum += previousRTA[i - 1];
                        }
                        previousRTA[previousRTA.Length - 1] = currentRR; //Save new RTA value
                        sum += currentRR;
                        averageRR = sum / previousRTA.Length;/// totalWeight; //Get average over smoothingSize weighted RTAs

                        if (Common.StartSensorLogging) //If logging
                        {
                            processedFile.WriteLine(DateTime.Now.ToString(Common.TimeFormat) + "," + (tickNow - 1) + "," + Math.Round(currentRR, 2) + "," + averageRR);//Write values to file
                        }
                    }
                }
            }
        }
    }
    float GetStd(float[] fY) //Used by GetRRzx
    {
        float avg = fY.Average();
        return (float)Math.Sqrt(fY.Average(v => Math.Pow(v - avg, 2)));
    }
    void CreateLogs() //Create log files using the ppn and session numbers
    {
        string pp = "";
        if (Common.Ppn < 10) { pp = "00" + Common.Ppn.ToString(); }
        if (Common.Ppn >= 10 && Common.Ppn < 100) { pp = "0" + Common.Ppn.ToString(); }
        if(Common.Ppn >= 100) { pp = Common.Ppn.ToString(); }

        //set file name
        File.Delete("Logfiles/RespRaw/RRraw_" + pp + ".txt"); //Delete if exists
        File.Delete("Logfiles/RespPrc/RRprc_" + pp + ".txt");
        File.Delete("Logfiles/RespAvg/RRavg_" + pp + ".txt");
        theRAWFile = new StreamWriter("Logfiles/RespRaw/RRraw_" + pp + ".txt"); //Create the RAW values log file
        averageFile = new StreamWriter("Logfiles/RespAvg/RRavg_" + pp + ".txt"); //Create the averaged (and STD) values log file
        processedFile = new StreamWriter("Logfiles/RespPrc/RRprc_" + pp + ".txt"); //Create the processed (bpm) values log file
    }
    void SetDataFiles()
    {
        string[] files = Directory.GetFiles("LogFiles\\Offline\\raw", "*.txt"); //The raw data file should be there
        string rawFilename = Path.GetFileName(files[0]);
        string avgFilename = rawFilename.Replace("raw", "avg");
        string prcFilename = rawFilename.Replace("raw", "prc");
        savedRAWfile = new StreamReader("LogFiles\\Offline\\raw\\" + rawFilename); //Open it and return the handle
        averageFile = new StreamWriter("LogFiles\\Offline\\avg\\" + avgFilename);
        processedFile = new StreamWriter("LogFiles\\Offline\\prc\\" + prcFilename);
    }
    void nowQuit()
    {
        if (Common.UsePlux) //Make sure we disconnect from the PLUX
        {
            PluxDevManager.StopAcquisitionUnity();
            PluxDevManager.DisconnectPluxDev();
        }
#if UNITY_EDITOR
UnityEditor.EditorApplication.isPlaying = false;
#else
Application.Quit();
#endif
    }
    private void OnApplicationQuit() //Make sure we close the log files
    {
        if (theRAWFile != null) theRAWFile.Close();
        if (processedFile != null)
        {
            processedFile.Close();
            averageFile.Close();
        }
    }
    public void Scan() //Start the scan for PLUX devices
    {
        try
        {
            List<string> listOfDomains = new List<string>() { "BTH" };// List of available Devices - classical bluetooth only
            PluxDevManager.GetDetectableDevicesUnity(listOfDomains);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.Log("Error scanning " + e.Message);
            Common.sensorStatus = "Error scanning";
            step = 999;
        }
    }
    public void Connect() //Start connection to PLUX device
    {
        try
        {
            this.SelectedDevice = Common.PluxMAC; //"00:07:80:F9:DD:CC";// 79:6F:CC"; // Get the selected device.
            UnityEngine.Debug.Log("Trying to establish a connection with device " + this.SelectedDevice);// Connection with the device.
            try
            {
                PluxDevManager.PluxDev(this.SelectedDevice);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.Log("Error connectin: " + e);// Print information about the exception.
                Common.sensorStatus = "Error connecting...";
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.Log("Error connectin: " + e);// Print information about the exception.
            Common.sensorStatus = "Error connecting...";
            step = 999;
        }
    }
    public void StartAcquisition() //Start acquisition, callback handles incoming data
    {
        try
        {
            List<PluxDeviceManager.PluxSource> pluxSources = new List<PluxDeviceManager.PluxSource>(); //Build list with sensors
            pluxSources.Add(new PluxDeviceManager.PluxSource(1, 1, Resolution, 0x01)); //Add analogue sensor to a port 1
            //pluxSources.Add(new PluxDeviceManager.PluxSource(2, 1, Resolution, 0x01)); //Add analogue sensor to a port 2
            //pluxSources.Add(new PluxDeviceManager.PluxSource(9, 1, Resolution, 0x03)); //Add fNIRS to virtual port 9

            PluxDevManager.StartAcquisitionBySourcesUnity(SamplingRate, pluxSources.ToArray()); //Start it

            //SamplingRate = 100;// Get Device Configuration input values.
            //int resolution = 8;
            //PluxDevManager.StartAcquisitionUnity(SamplingRate, ActiveChannels, Resolution);
            Common.sensorStatus = "ACQ";
            step = 500;
        }
        catch (Exception exc)
        {
            UnityEngine.Debug.Log("Error starting acquisition: " + exc.Message + "\n" + exc.StackTrace);
            Common.sensorStatus = "Error";
            step = 999;
        }
    }
    public void ScanResults(List<string> listDevices) //Called after a Scan()
    {
        this.ListDevices = listDevices;// Store list of devices in a global variable.
        UnityEngine.Debug.Log("Number of Detected Devices: " + this.ListDevices.Count);// Info message for development purposes.
        for (int i = 0; i < this.ListDevices.Count; i++)
        {
            Console.WriteLine("Device--> " + this.ListDevices[i]);
        }
        Common.sensorStatus = "Scanning succesful.";
        step = 100;
    }
    public void ConnectionDone(bool connectionStatus) //Called when connected
    {
        UnityEngine.Debug.Log("Connection with device " + this.SelectedDevice + " established with success!");
        UnityEngine.Debug.Log("Product ID: " + PluxDevManager.GetProductIdUnity());
        Common.sensorStatus = "OK";
        step = 300;
    }
    public void AcquisitionStarted(bool acquisitionStatus, bool exceptionRaised = false, string exceptionMessage = "") 
    {
        if (acquisitionStatus) 
        {
            UnityEngine.Debug.Log("Acquisition has started...");
            Common.SensorRunning = true;
        }
    }
    public void OnEventDetected(PluxDeviceManager.PluxEvent pluxEvent) 
    {
        
        UnityEngine.Debug.Log("Some event happened: " + pluxEvent.ToString()); // (pluxEvent as PluxDeviceManager.PluxDisconnectEvent).reason);
    }
    public void OnExceptionRaised(int exceptionCode, string exceptionDescription) 
    {
        UnityEngine.Debug.Log("Some error: " + exceptionDescription);
    }
    void SetupLogFileFolders()
    {
        if (!Directory.Exists("Logfiles")) { Directory.CreateDirectory("Logfiles"); }
        if (!Directory.Exists("Logfiles/RespRaw")) { Directory.CreateDirectory("Logfiles/RespRaw"); }
        if (!Directory.Exists("Logfiles/RespPrc")) { Directory.CreateDirectory("Logfiles/RespPrc"); }

        if (!Directory.Exists("Logfiles/RespAvg")) { Directory.CreateDirectory("Logfiles/RespAvg"); }
    }
    bool TestPPN(int inputPPN)
    {
        bool result = false;
        print("testing ppn -" + inputPPN + "-");
        if (inputPPN > 0)
        {
            if (!Directory.Exists("Settings"))
            {
                Directory.CreateDirectory("Settings");
            }
            if (!File.Exists("Settings\\ppnlist.txt")) //Test for PPN list, if not there
            {
                StreamWriter listppn = new StreamWriter("Settings\\ppnlist.txt"); //Create a new one
                listppn.WriteLine(inputPPN); //Write out the entered PPN
                listppn.Close();
                Common.Ppn = inputPPN; //Set Common var
                result = true;
            }
            else
            {
                string line = string.Empty; //Pre def
                bool foundPPN = false; //Pre def
                StreamReader listppn = new StreamReader("Settings\\ppnlist.txt"); //Open ppn list
                while ((line = listppn.ReadLine()) != null) //Read through file
                {
                    //print("Test list file: -" + line + "- and -" + inputPPN + "-");
                    if (Convert.ToInt32(line) == inputPPN) { foundPPN = true; break; } //If new PPN already in list? Flag and exit
                }
                listppn.Close();
                if (foundPPN) //Entered PPN is already there - note error and erase field
                {
                    txtStatus.text = "Number already exists. Please enter a new one.";
                    Common.Ppn = 0;
                }
                else //PPN is new
                {
                    //txtError.SetActive(false); //Turn error message off just in case
                    StreamWriter writePPN = new StreamWriter("Settings\\ppnlist.txt", true); //Open with append
                    writePPN.WriteLine(inputPPN); //Write new ppn
                    writePPN.Close();
                    Common.Ppn = Convert.ToInt32(inputPPN); //Set Common
                    result = true;
                }
            }
        }
        print("Final PPN: " + Common.Ppn);
        return result;
    }
    public void ProcessButton(GameObject btn)
    {
        switch (btn.name)
        {
            case "btnPPN":
                if(TestPPN(Convert.ToInt32(inpPPN.text))) 
                { 
                    Common.Ppn = Convert.ToInt32(inpPPN.text);
                    pnlPPN.SetActive(false);
                }
                break;
        }
    }
    void ReadSettingsFile() //Read the settings.txt file
    {
        StreamReader settingsFile = new StreamReader("Settings/settings.txt");
        string line = "";
        int counter = 0;
        while ((line = settingsFile.ReadLine()) != null)
        {
            string[] elements = line.Split(':');
            switch (counter)
            {
                case 0: //PLUX MAC
                    Common.PluxMAC = elements[1].Replace('-', ':');
                    break;
                case 1: //use PLUX?
                    Common.UsePlux = Convert.ToBoolean(elements[1]);
                    if (Common.UsePlux)
                    {
                        Debug.Log("USING plux");
                    }
                    else
                    {
                        Debug.Log("NOT using plux");
                    }
                    break;
                case 2: //Lowest valid RR
                    Common.AbsRRminimum = (float)Convert.ToDouble(elements[1]);
                    //Debug.Log("ABS Min RR: " + Common.AbsRSPminimum);
                    break;
                case 3: //Highest valid RR
                    Common.AbsRRmaximum = (float)Convert.ToDouble(elements[1]);
                    //Debug.Log("ABS Max RR: " + Common.AbsRSPmaximum);
                    break;
                case 4: //Whether or not we restrict the rate of change in the average signal
                    Common.ClampedAVG = Convert.ToBoolean(elements[1]);
                    break;
            }
            counter++;
        }
        settingsFile.Close();
    }
}
