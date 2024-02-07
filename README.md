# BreathingSensor
Unity-based algorithm for the Biosignals Plux Piezo breathing sensor

Requirements
* Plux analogue sensor hub, although the algorithm can of course be used for other sensors too
* A respiratory piezo sensro from Plux connected to port 1 (these things can obviously be changed in the code)
* Note: a specific MAC address is needed per Plux hub
* In Settings/settings.txt several settings are exposed (see below)
* In Assets/Scripts/Common.cs several hyper-global static variables are used for easy access

The code connects to the defined sensor hub (MAC address in settings.txt). Then tries to use port 1 to collect data. Data is sampled at 10Hz with 8bit resolution.
The code uses a 20 data point (=2.0 sec) lag and stores these points using a weight: 0.1 * current point + 0.9 * previous stored point.
The array with 20 (=lag) points is averaged and stored in a new array with 20 length.
A simple peak and valley detection is done: Is current point < previous point, then previous point is a peak
When at least 2 peaks and valleys have been detected the periods (peaks and valleys) is calculated into breaths-per-minute.
These are clamped between min-max values (settings.txt) and the maximum rate of change (at 10Hz every 0.1sec) is clamped to. This is hard coded to 1.5.

Raw, averaged and processed (breaths-per-minute) values are all written out to text log files.

In the settings.txt you can set:
* Mac adddress of the sensor
* Using the sensor or not. If not a hard coded raw file reference is used to read a previously saved file and analyze this to averaged and processed data
* Min/Max breaths-per-minute values for clamping
* And a bool to set the rate-of-change clamp on or off

