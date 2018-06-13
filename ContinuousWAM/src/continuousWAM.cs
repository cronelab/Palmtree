﻿/**
 * The continuousWAM class
 * 
 * ...
 * 
 * 
 * Copyright (C) 2017:  RIBS group (Nick Ramsey Lab), University Medical Center Utrecht (The Netherlands) & external contributors
 * Concept:             UNP Team                    (neuroprothese@umcutrecht.nl)
 * Author(s):           Benny van der Vijgh         (benny@vdvijgh.nl)
 * 
 * This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software
 * Foundation, either version 3 of the License, or (at your option) any later version. This program is distributed in the hope that it will be useful, but
 * WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for
 * more details. You should have received a copy of the GNU General Public License along with this program. If not, see <https://www.gnu.org/licenses/>.
 */

using NLog;
using System;
using System.Collections.Generic;
using System.Threading;
using UNP.Applications;
using UNP.Core;
using UNP.Core.Helpers;
using UNP.Filters;
using UNP.Core.Params;
using UNP.Core.DataIO;
using System.Collections.Specialized;

namespace continuousWAM {

    /// <summary>
    /// The <c>continuousWAM</c> class.
    /// 
    /// ...
    /// </summary>
    
    public class continuousWAM : IApplication, IApplicationUNP {

		private enum TaskStates:int {
			Wait,
			CountDown,
			ColumnSelect,
			ColumnSelected,
            EscapeCue,
			EndText
		};

        public enum scoreTypes : int {
            TruePositive,
            FalsePositive,
            FalseNegative,
            TruePositiveEscape,
            FalseNegativeEscape
        };

        private const int CLASS_VERSION = 1;
        private const string CLASS_NAME = "continuousWAM";
        private const string CONNECTION_LOST_SOUND = "sounds\\connectionLost.wav";

        private static Logger logger = LogManager.GetLogger(CLASS_NAME);            // the logger object for the view
        private static Parameters parameters = null;
        
        private int inputChannels = 0;
        private CWAMView view = null;

        private Random rand = new Random(Guid.NewGuid().GetHashCode());
        private Object lockView = new Object();                                     // threadsafety lock for all event on the view
        private bool taskPaused = false;								            // flag to hold whether the task is pauzed (view will remain active, e.g. connection lost)

        private bool unpMenuTask = false;								            // flag whether the task is created by the UNPMenu
        private bool unpMenuTaskRunning = false;						            // flag to hold whether the task should is running (setting this to false is also used to notify the UNPMenu that the task is finished)
        private bool umpMenuTaskSuspended = false;						            // flag to hold whether the task is suspended (view will be destroyed/re-initiated)

        private bool connectionLost = false;							            // flag to hold whether the connection is lost
        private bool connectionWasLost = false;						                // flag to hold whether the connection has been lost (should be reset after being re-connected)

        // task input parameters
        private int windowLeft = 0;
        private int windowTop = 0;
        private int windowWidth = 800;
        private int windowHeight = 600;
        private int windowRedrawFreqMax = 0;
        private RGBColorFloat windowBackgroundColor = new RGBColorFloat(0f, 0f, 0f);

        private int taskInputChannel = 1;											// input channel
        private int taskFirstRunStartDelay = 0;                                    // the first run start delay in sample blocks
        private int taskStartDelay = 0;                                            // the run start delay in sample blocks
        private int countdownTime = 0;                                             // the time the countdown takes in sample blocks

        private bool keySequenceActive = false;
        private bool keySequenceWasPressed = false;
        private int waitCounter = 0;
        private int columnSelectDelay = 0;
        private int columnSelectedDelay = 0;
        private int[] fixedTrialSequence = new int[0];                              // target sequence (input parameter)
        private bool showScore = false;
        private int taskMode = 0;                                                   // the mode used: 1: Continuous WAM, 2: Continuous WAM with computer help, 3: Dynamic mode
        private int dynamicParameter = 0;                                           // Parameter to be optimised in Dynamic Mode. 1: Threshold, 2: Active Rate, 3: Active Period, 4: Mean, 5: ColumnSelectDelay  


        // task (active) variables
        private List<MoleCell> holes = new List<MoleCell>(0);
        private TaskStates taskState = TaskStates.Wait;
        private TaskStates previousTaskState = TaskStates.Wait;

        private int holeRows = 1;
        private int holeColumns = 8;
        private int minMoleDistance = 0;
        private int maxMoleDistance = 0;
        private int currentRowID = 0;
        private int currentColumnID = 0;
        private int numberOfMoles = 1;
        private int numberOfEscapes = 0;
        private int escapeInterval = 0;                                             // minimal amount of moles between consecutive escape cues
        private int escapeDuration = 0;                                             
        private List<int> cueSequence = new List<int>(0);		                    // the cue sequence being used in the task (can either be given by input or generated)
        private int currentMoleIndex = -1;							                // specify the position of the mole (grid index)
        private int currentCueIndex = 0;						                    // specify the position in the random sequence of trials
        private int countdownCounter = 0;					                        // the countdown timer
        private int score = 0;						                                // the score of the user hitting a mole
        private List<scoreTypes> posAndNegs = new List<scoreTypes>(0);                  // list holding the different scores aggregated

        // computer help mode
        private List<bool> helpClickVector = null;
        private int posHelpPercentage = 0;                                          // percentage of samples that will be corrected: if a false negative no-click is made during such a sample, it will be corrected into a true positive
        private int negHelpPercentage = 0;                                          // percentage of samples that will be corrected: if a false positive click is made during such a sample, it will be corected into a true negative 
        ClickTranslatorFilter clickTranslator = null;                               // reference to clicktranslator filter to set or unset refractoryPeriod

        // dynamic mode
        private bool firstUpdate = true;
        private string filter = "";
        private string param = "";
        private Parameters dynamicParameterSet = null;
        private string paramType = null;
        private scoreTypes increaseType = scoreTypes.FalsePositive;
        private scoreTypes decreaseType = scoreTypes.FalseNegative;
        private dynamic localParamCopy = null;
        private int addInfo = 0;
        private int stopAfterCorrect = 0;
        private int currentCorrect = 0;
        private double stepSize = 0;                                                // stepsize with which the dynamic parameter is being adjusted per step

        public continuousWAM() : this(false) { }
        public continuousWAM(bool UNPMenuTask) {

            // transfer the UNP menu task flag
            unpMenuTask = UNPMenuTask;
            
            // check if the task is standalone (not unp menu)
            if (!unpMenuTask) {

                // create a parameter set for the task
                parameters = ParameterManager.GetParameters(CLASS_NAME, Parameters.ParamSetTypes.Application);

                // define the parameters
                defineParameters(ref parameters);
            }

            // message
            logger.Info("Application " + CLASS_NAME + " created (version " + CLASS_VERSION + ")");
        }

        private void defineParameters(ref Parameters parameters) {

            // define the parameters
            parameters.addParameter<int>(
                "WindowLeft",
                "Screen coordinate of application window's left edge",
                "", "", "0");

            parameters.addParameter<int>(
                "WindowTop",
                "Screen coordinate of application window's top edge",
                "", "", "0");

            parameters.addParameter<int>(
                "WindowWidth",
                "Width of application window (fullscreen and 0 will take monitor resolution)",
                "", "", "800");

            parameters.addParameter<int>(
                "WindowHeight",
                "Height of application window (fullscreen and 0 will take monitor resolution)",
                "", "", "600");

            parameters.addParameter<int>(
                "WindowRedrawFreqMax",
                "Maximum display redraw interval in FPS (0 for as fast as possible)",
                "0", "", "50");

            parameters.addParameter<RGBColorFloat>(
                "WindowBackgroundColor",
                "Window background color",
                "", "", "0");

            parameters.addParameter<int>(
                "Mode",
                "1: Continuous WAM (CWAM), 2: CWAM with computer help, 3: Dynamic mode",
                "0", "", "1");

            parameters.addParameter<int>(
               "PositiveHelpPercentage",
               "Only in CWAM with computer help: percentage of samples during cell selection that will be corrected if a false negative no-click is made during that sample",
               "0", "", "40");

            parameters.addParameter<int>(
               "NegativeHelpPercentage",
               "Only in CWAM with computer help: percentage of samples during cell selection that will be corrected if a false positive click is made during that sample",
               "0", "", "10");

            parameters.addParameter<int>(
                "DynamicParameter",
                "Only in Dynamic Mode: parameter to be optimised. 1: Threshold, 2: Active Rate, 3: Active Period, 4: Mean, 5: ColumnSelectDelay",
                "0", "", "1");

            parameters.addParameter<double>(
                "Stepsize",
                "Only in Dynamic Mode: stepsize with which dynamic parameter is adjusted per step, relative to current value of paramter, in %",
                "0", "", "5");

            parameters.addParameter<int>(
               "StopAfterCorrect",
               "Only in Dynamic Mode: after how many correct responses in a row the task will end. Set to 0 to not end task based on amount of correct responses",
               "0", "", "1");

            parameters.addParameter<int>(
                "TaskFirstRunStartDelay",
                "Amount of time before the task starts (on the first run of the task)",
                "0", "", "5s");

            parameters.addParameter<int>(
                "TaskStartDelay",
                "Amount of time before the task starts (after the first run of the task)",
                "0", "", "5s");

            parameters.addParameter<int>(
                "CountdownTime",
                "Amount of time the countdown before the task takes",
                "0", "", "3s");

            parameters.addParameter<int>(
                "TaskInputChannel",
                "Channel to base the cursor position on  (1...n)",
                "1", "", "1");

            parameters.addParameter<double>(
                "ColumnSelectDelay",
                "Amount of time before continuing to next column",
                "0", "", "3s");

            parameters.addParameter<double>(
                "ColumnSelectedDelay",
                "Amount of time after selecting a column to wait",
                "0", "", "1s");

            parameters.addParameter<int>(
                "NumberOfMoles",
                "Amount of moles presented",
                "1", "", "10");

            parameters.addParameter<int>(
                "MinimalMoleDistance",
                "Minimal amount of cells between appearing mole and currently selected cell",
                "1", "", "3");

            parameters.addParameter<int>(
               "MaximalMoleDistance",
               "Maximal amount of cells between appearing mole and currently selected cell",
               "1", "", "8");

            parameters.addParameter<int>(
                "NumberOfEscapes",
                "Amount of Escape cues presented",
                "1", "", "2");

            parameters.addParameter<int>(
                "EscapeInterval",
                "Minimum amount of moles between consecutive Escape cues",
                "1", "", "2");

            parameters.addParameter<double>(
                "EscapeDuration",
                "Amount of time escape cue is presented",
                "0", "", "3s");

            parameters.addParameter<int[]>(
                "TrialSequence",
                "Fixed sequence in which targets should be presented (leave empty for random). \nNote. the 'NumberOfTrials' parameter will be overwritten with the amount of values entered here",
                "0", "", "");

            parameters.addParameter<bool>(
                "ShowScore",
                "Enable/disable showing of scoring",
                "1");

        }

        public Parameters getParameters() {
            return parameters;
        }

        public string getClassName() {
            return CLASS_NAME;
        }

        public int getClassVersion() {
            return CLASS_VERSION;
        }

        public bool configure(ref PackageFormat input) {

            // store the number of input channels
            inputChannels = input.getNumberOfChannels();

            // check if the number of input channels is higher than 0
            if (inputChannels <= 0) {
                logger.Error("Number of input channels cannot be 0");
                return false;
            }

            // configure the parameters
            return configure(parameters);

        }


        public bool configure(Parameters newParameters) {
            
            // 
            // TODO: parameters.checkminimum, checkmaximum
            //

            // retrieve window settings
            windowLeft = newParameters.getValue<int>("WindowLeft");
            windowTop = newParameters.getValue<int>("WindowTop");
            windowWidth = newParameters.getValue<int>("WindowWidth");
            windowHeight = newParameters.getValue<int>("WindowHeight");
            windowRedrawFreqMax = newParameters.getValue<int>("WindowRedrawFreqMax");
            windowBackgroundColor = newParameters.getValue<RGBColorFloat>("WindowBackgroundColor");

            if (windowRedrawFreqMax < 0) {
                logger.Error("The maximum window redraw frequency can be no smaller then 0");
                return false;
            }
            if (windowWidth < 1) {
                logger.Error("The window width can be no smaller then 1");
                return false;
            }
            if (windowHeight < 1) {
                logger.Error("The window height can be no smaller then 1");
                return false;
            }

            // retrieve the input channel setting
            taskInputChannel = newParameters.getValue<int>("TaskInputChannel");
            if (taskInputChannel < 1) {
                logger.Error("Invalid input channel, should be higher than 0 (1...n)");
                return false;
            }
            if (taskInputChannel > inputChannels) {
                logger.Error("Input should come from channel " + taskInputChannel + ", however only " + inputChannels + " channels are coming in");
                return false;
            }

            // retrieve the task mode
            taskMode = newParameters.getValue<int>("Mode");
            if (taskMode < 1 || taskMode > 3) {
                logger.Error("Only task modes between 1 and 3 are allowed.");
                return false;
            }

            // retrieve the task delays 
            taskFirstRunStartDelay = newParameters.getValueInSamples("TaskFirstRunStartDelay");
            taskStartDelay = newParameters.getValueInSamples("TaskStartDelay");
            if (taskFirstRunStartDelay < 0 || taskStartDelay < 0) {
                logger.Error("Start delays cannot be less than 0");
                return false;
            }

            // retrieve the countdown time
            countdownTime = newParameters.getValueInSamples("CountdownTime");
            if (countdownTime < 0) {
                logger.Error("Countdown time cannot be less than 0");
                return false;
            } 

            // retrieve selection delays
            columnSelectDelay = newParameters.getValueInSamples("ColumnSelectDelay");
            columnSelectedDelay = newParameters.getValueInSamples("ColumnSelectedDelay");
            if (columnSelectDelay < 1 || columnSelectedDelay < 1) {
                logger.Error("The 'ColumnSelectDelay' or 'ColumnSelectedDelay' parameters should not be less than 1");
                return false;
            } 

            // retrieve the number of moles
            numberOfMoles = newParameters.getValue<int>("NumberOfMoles");
            if (numberOfMoles < 1) {
                logger.Error("Minimum of 1 mole is required");
                return false;
            }

            // retrieve minimal distance between current cell and appearing moles
            minMoleDistance = newParameters.getValue<int>("MinimalMoleDistance");
            if (minMoleDistance < 1) {
                logger.Error("Minimal distance of 1 cell is required");
                return false;
            }

            // retrieve maximal distance between current cell and appearing moles
            maxMoleDistance = newParameters.getValue<int>("MaximalMoleDistance");
            if (maxMoleDistance <= minMoleDistance) {
                logger.Error("Maximal distance needs to be larger than minimal distance");
                return false;
            }

            // retrieve the number of escape cues and interval between cues
            numberOfEscapes = newParameters.getValue<int>("NumberOfEscapes");
            escapeInterval = newParameters.getValue<int>("EscapeInterval");
            if ( ((numberOfEscapes-1) * escapeInterval) > numberOfMoles) {
                logger.Error("To present " + numberOfEscapes + " Escape cues with " + escapeInterval + " moles between the cues, at least " + ((numberOfEscapes - 1) * escapeInterval) + " moles are needed. Adjust 'Number of moles' parameter accordingly");
                return false;
            }

            // retrieve how long escape cue is presented 
            escapeDuration = newParameters.getValueInSamples("EscapeDuration");
            if (escapeDuration <= 0 ) {
                logger.Error("Escape cue must be presented at least one sample.");
                return false;
            }

            // retrieve whether to show score
            showScore = newParameters.getValue<bool>("ShowScore");

            // retrieve (fixed) trial sequence
            fixedTrialSequence = newParameters.getValue<int[]>("TrialSequence");
            if (fixedTrialSequence.Length > 0) {
                int numHoles = holeRows * holeColumns;
                for (int i = 0; i < fixedTrialSequence.Length; ++i) {
                    if (fixedTrialSequence[i] < 0) {
                        logger.Error("The TrialSequence parameter contains a target index (" + fixedTrialSequence[i] + ") that is below zero, check the TrialSequence");
                        return false;
                    }
                    if (fixedTrialSequence[i] >= numHoles) {
                        logger.Error("The TrialSequence parameter contains a target index (" + fixedTrialSequence[i] + ") that is out of range, check the HoleRows and HoleColumns parameters. (note that the indexing is 0 based)");
                        return false;
                    }
                    // TODO: check if the mole is not on an empty spot
                }
            }

            // configure buffers for computer help mode
            if (taskMode == 2) {

                // create help click vector to hold help clicks
                helpClickVector = new List<bool>(new bool[columnSelectDelay]);

                // retrieve help percentages
                posHelpPercentage = newParameters.getValue<int>("PositiveHelpPercentage");
                negHelpPercentage = newParameters.getValue<int>("NegativeHelpPercentage");
                if (posHelpPercentage < 1 || posHelpPercentage > 100 || negHelpPercentage < 1 || negHelpPercentage > 100) {
                    logger.Error("Positive and negative help percentages can not be below 0% or above 100%");
                    return false;
                }

                // get reference to clickTranslator filter from mainThread
                List<IFilter> filters = MainThread.getFilters();
                for (int i = 0; i < filters.Count; i++)  if (filters[i].getName() == "ClickTranslator") clickTranslator = (ClickTranslatorFilter)filters[i];

            }

            // retrieve parameters for dynamic mode 
            dynamicParameter = newParameters.getValue<int>("DynamicParameter");
            stepSize = newParameters.getValue<double>("Stepsize");
            stopAfterCorrect = newParameters.getValue<int>("StopAfterCorrect");

            // perform checks on parameters for dynamic mode, if mode is set to dynamic mode
            if (taskMode == 3) {

                // amount of correct responses should be positive, but less than total amount of moles presented         
                if (stopAfterCorrect < 0 || stopAfterCorrect > numberOfMoles) {
                    logger.Error("The required amount of correct responses to end the task needs to be larger than 0, and less than the total amount of moles presented.");
                    return false;
                }

                // stepsize needs to be positive, but below 100%
                if (stepSize < 1 || stepSize > 100) {
                    logger.Error("Stepsize can not be below 0% or above 100%");
                    return false;
                }

                // check range parameter: we only have 5 dynamic paramters
                if ((dynamicParameter < 1 || dynamicParameter > 5)) {
                    logger.Error("Only dynamic parameter values between 1 and 5 are allowed.");
                    return false;
                }

                // if dynamic paramter is the mean, check in adaptation filter if this filter is also trying to optimize the mean
                if (dynamicParameter == 4) {

                    // retrieve adaptation settings from Adaptationfilter
                    Parameters adapParams = MainThread.getFilterParametersClone("Adaptation");
                    int[] adaptations = adapParams.getValue<int[]>("Adaptation");

                    // set temp bool
                    bool proceed = true;

                    // cycle through adaptation settings for the different channels to check if there is a channel that is set to adaptation
                    for (int i = 0; i < adaptations.Length; i++)
                        if (adaptations[i] != 1) proceed = false;

                    // if there exists channels set to adaptation, give feedback and return
                    if (!proceed) {
                        logger.Error("The adaptationfilter is either set to no adaptation at all, or set to calibration (ie the adaptation parameter of this filter is 0, or larger than 1). It is not possible to optimize the mean if the adaptationfilter is set to no adaptation, or to attempt to optimize the mean both in the adaptationfilter and with this task.");
                        return false;
                    }
                }
            }

            // return success
            return true;

        }

        public void initialize() {
                                
            // lock for thread safety
            lock(lockView) {

                // calculate the cell holes for the task
                int numHoles = holeRows * holeColumns;

                // create the array of cells for the task
                holes = new List<MoleCell>(0);
                for (int i = 0; i < numHoles; i++) {
                        holes.Add(new MoleCell(0, 0, 0, 0, MoleCell.CellType.Hole));
                }

                // check the view (thread) already exists, stop and clear the old one.
                destroyView();

                // initialize the view
                initializeView();

                // check if a target sequence is set
	            if (fixedTrialSequence.Length == 0) {
		            // trialSequence not set in parameters, generate
		            
		            // Generate targetlist
		            generateCueSequence();

	            } else {
		            // trialsequence is set in parameters

                    // clear the trials
		            if (cueSequence.Count != 0)		cueSequence.Clear();
                
		            // transfer the fixed trial sequence
                    cueSequence = new List<int>(fixedTrialSequence);

	            }	            
            }
        }

        private void initializeView() {

            // create the view
            view = new CWAMView(windowRedrawFreqMax, windowLeft, windowTop, windowWidth, windowHeight, false);
            view.setBackgroundColor(windowBackgroundColor.getRed(), windowBackgroundColor.getGreen(), windowBackgroundColor.getBlue());

            // set task specific display attributes 
            view.setFixation(false);                                            // hide the fixation
            view.setCountDown(-1);                                              // hide the countdown
            view.viewScore(showScore);                                          // show/hide score according to parameter setting

            // initialize the holes for the scene
            view.initGridPositions(holes, holeRows, holeColumns, 10);

            // initialize the score grid
            view.initScoreGrid(numberOfMoles, numberOfEscapes, holes);

            // start the scene thread
            view.start();

            // wait till the resources are loaded or a maximum amount of 30 seconds (30.000 / 50 = 600)
            // (resourcesLoaded also includes whether GL is loaded)
            int waitCounter = 600;
            while (!view.resourcesLoaded() && waitCounter > 0) {
                Thread.Sleep(50);
                waitCounter--;
            }

        }

        public void start() {

            // check if the task is standalone (not unp menu)
            if (!unpMenuTask) {
                
                // store the generated sequence in the output parameter xml
                Data.adjustXML(CLASS_NAME, "TrialSequence", string.Join(" ", cueSequence));
                
            }

            // lock for thread safety
            lock(lockView) {

                if (view == null)   return;

                // log event task is started
                Data.logEvent(2, "TaskStart", CLASS_NAME);

                // reset the score
                score = 0;

	            // reset countdown to the countdown time
	            countdownCounter = countdownTime;

	            if(taskStartDelay != 0 || taskFirstRunStartDelay != 0) {

		            // set state to wait
		            setState(TaskStates.Wait);

                    // show the fixation
                    view.setFixation(true);

	            } else {
			
		            // countdown
		            setState(TaskStates.CountDown);

	            }

            }

        }

        public void stop() {
            
            // stop the connection lost sound from playing
            SoundHelper.stopContinuous();

            // lock for thread safety
            lock (lockView) {

                // stop the task
                stopTask();

            }

            // log event app is stopped
            Data.logEvent(2, "AppStopped", CLASS_NAME);

        }

        public bool isStarted() {
            return true;
        }

        public void process(double[] input) {

            // retrieve the connectionlost global
            connectionLost = Globals.getValue<bool>("ConnectionLost");

            // process input
            process(input[taskInputChannel - 1]);

        }

        private void process(double input) {
            
            // lock for thread safety
            lock (lockView) {
                
                if (view == null)   return;

                ////////////////////////
                // BEGIN CONNECTION FILTER ACTIONS//
                ////////////////////////

                // check if connection is lost, or was lost
                if (connectionLost) {

                    // check if it was just discovered if the connection was lost
                    if (!connectionWasLost) {

                        // set the connection as was lost (this also will make sure the lines in this block willl only run once)
                        connectionWasLost = true;

                        // pause the task
                        pauseTask();

			            // show the lost connection warning
			            view.setConnectionLost(true);

                        // play the connection lost sound continuously every 2 seconds
                        SoundHelper.playContinuousAtInterval(CONNECTION_LOST_SOUND, 2000);

                    }

                    // do not process any further
                    return;
            
                } else if (connectionWasLost && !connectionLost) {

                    // stop the connection lost sound from playing
                    SoundHelper.stopContinuous();

                    // hide the lost connection warning
                    view.setConnectionLost(false);

                    // resume task
                    resumeTask();

                    // reset connection lost variables
                    connectionWasLost = false;

                }

                ////////////////////////
                // END CONNECTION FILTER ACTIONS//
                ////////////////////////

	            // check if the task is pauzed, do not process any further if this is the case
	            if (taskPaused)		    return;
                
                // checked if there is a escape made
                keySequenceActive = Globals.getValue<bool>("KeySequenceActive");

                // log if the escapestate has changed
                if (keySequenceActive != keySequenceWasPressed) {
                    Data.logEvent(2, "EscapeChange", (keySequenceActive) ? "1" : "0");
                    keySequenceWasPressed = keySequenceActive;
                }

                // check if there is a click
                bool click = (input == 1 && !keySequenceActive);

                // use the task state
                switch (taskState) {

                    // starting, pauzed or waiting
                    case TaskStates.Wait:

                        // set the state to countdown
                        if (waitCounter == 0) {
                            setState(TaskStates.CountDown);
			            } else
				            waitCounter--;

                        break;

                    // Countdown before start of task
                    case TaskStates.CountDown:
                        
                        // check if the task is counting down
                        if (countdownCounter > 0) {

                            // display the countdown
                            view.setCountDown((int)Math.Floor((countdownCounter - 1) / MainThread.getPipelineSamplesPerSecond()) + 1);

                            // reduce the countdown timer
                            countdownCounter--;

                        // done counting down
                        } else {
				            
				            // hide the countdown counter
				            view.setCountDown(-1);

				            // begin first trial, and set the mole and selectionbox at the right position
				            currentCueIndex = 0;
                            currentColumnID = 0;

                            // Show hole grid and score
                            view.setGrid(true);
                            view.viewScore(true);

                            // log event countdown is started
                            Data.logEvent(2, "TrialStart ", CLASS_NAME);

                            // set next cue and corresponding state
                            setCueAndState(cueSequence[currentCueIndex]);
			            }

			            break;

                    // highlighting columns
                    case TaskStates.ColumnSelect:

                        // get whether the current column contains a mole
                        bool containsMole = currentMoleIndex == holeColumns * currentRowID + currentColumnID;

                        // if in computer help mode and we are not moving on to next column, combine click with computer help (no-)click
                        if (taskMode == 2 && waitCounter != 0) {

                            logger.Info("At sample " + waitCounter + " in this column the click is: " + click);

                            // get computer help click or no click
                            bool helpclick = helpClickVector[columnSelectDelay - waitCounter];
                            bool newClick = false;

                            // combine help click with actual click made, depending on the current column 
                            if (containsMole)   newClick = helpclick || click;
                            else                newClick = helpclick && click;

                            // if we changed click, set or unset refractoryperiod accordingly
                            if (newClick != click) {
                                if (newClick)   clickTranslator.setRefractoryPeriod(true);
                                else            clickTranslator.setRefractoryPeriod(false);
                            }

                            // set click to adjusted click
                            click = newClick;

                            logger.Info("and is adjusted to: " + click);
                        }

                        // if clicked
                        if (click) {
                            setState(TaskStates.ColumnSelected);
			            } else {
                            
                            // if time to highlight column has passed
                            if (waitCounter == 0) {

                                // if we missed a mole, store a false negative, and go to next cue
                                if (containsMole) {

                                    // store false negative
                                    posAndNegs.Add(scoreTypes.FalseNegative);

                                    // increase cue index
                                    currentCueIndex++;

                                    // if at end of cue sequence, go to Endtext state, otherwise set next cue and correpsonding state
                                    if (currentCueIndex == cueSequence.Count)       setState(TaskStates.EndText);
                                    else                                            setCueAndState(cueSequence[currentCueIndex]);

                                    // if in dynamic mode, adjust dynamic parameter and check if we need to stop task because enough correct responses have been given
                                    if (taskMode == 3) updateParameter();

                                // if no mole was missed, go to next cell and reset time 
                                } else {

                                    // advance to next cell
                                    currentColumnID++;

                                    // if the end of row has been reached, reset column id
                                    if (currentColumnID >= holeColumns) currentColumnID = 0;

                                    // re-set state to same state, to trigger functions that occur at beginning of processing of this state
                                    setState(TaskStates.ColumnSelect);
                                }

				            } else 
					            waitCounter--;
			            }

			            break;

                    // column was selected
                    case TaskStates.ColumnSelected:
			            
			            if(waitCounter == 0) {

                            // if mole is selected, store true positive
                            if (currentMoleIndex == holeColumns * currentRowID + currentColumnID) {

                                // store true positive
                                posAndNegs.Add(scoreTypes.TruePositive);

                                // go to next trial in the sequence and set mole and selectionbox
                                currentCueIndex++;
                                currentColumnID++;
                                if (currentColumnID >= holeColumns) currentColumnID = 0;

                                // check whether at the end of trial sequence
                                if (currentCueIndex == cueSequence.Count) {

                                    // show end text
                                    setState(TaskStates.EndText);

                                } else {

                                    // set next cue and corresponding state
                                    setCueAndState(cueSequence[currentCueIndex]);

                                }

                                // if in dynamic mode, adjust dynamic parameter and check if we need to stop task because enough correct responses have been given
                                if (taskMode == 3) updateParameter();

                            // no hit, store false positive
                            } else {

                                // store false positive
                                posAndNegs.Add(scoreTypes.FalsePositive);

                                // start again selecting rows from the top
                                setState(TaskStates.ColumnSelect);

                                // if in dynamic mode, adjust dynamic parameter and check if we need to stop task because enough correct responses have been given
                                if (taskMode == 3) updateParameter();
                            }

			            } else
				            waitCounter--;

			            break;

                    // escape cue is presented
                    case TaskStates.EscapeCue:

                        // if escape cue has been presented for the complete duration, log false negative and go to next mole or escape
                        if (waitCounter == 0 || keySequenceActive) {

                            // store or true positive or false negative depending on whether an escape sequence was made
                            if (keySequenceActive)  posAndNegs.Add(scoreTypes.TruePositiveEscape);
                            else                    posAndNegs.Add(scoreTypes.FalseNegativeEscape);

                            // remove escape cue
                            view.setEscape(false);

                            // go to next trial in the sequence and check whether at the end of trial sequence
                            currentCueIndex++;
                            if (currentCueIndex == cueSequence.Count)   setState(TaskStates.EndText);       
                            else                                        setCueAndState(cueSequence[currentCueIndex]);

                            // if in dynamic mode, adjust dynamic parameter and check if we need to stop task because enough correct responses have been given
                            if (taskMode == 3) updateParameter();

                        } else
                            waitCounter--;
                        
                        break;

                    // end text
                    case TaskStates.EndText:
			            
			            if (waitCounter == 0) {

                            // log event task is stopped
                            Data.logEvent(2, "TaskStop", CLASS_NAME + ";end");

                            // stop the task, this will also call stop(), and as a result stopTask()
                            if (unpMenuTask)        UNP_stop();
                            else                    MainThread.stop(false);

                        } else
				            waitCounter--;

			            break;

	            }

                // update the score 
                updateScore();

            }

        }

        public void destroy() {
            
            // stop the application
            // Note: At this point stop will probably have been called from the mainthread before destroy, however there is a slight
            // chance that in the future someone accidentally will put something in the configure/initialize that should have
            // actually been put in the start. If start is not called in the mainthread, then stop will also not be called at the
            // modules. For these accidents we do an extra stop here.
            stop();

            // lock for thread safety
            lock(lockView) {

                // destroy the view
                destroyView();

            }

            // destroy/empty more task variables


        }


        // pauses the task
        private void pauseTask() {
	        if (view == null)   return;

            // log event task is paused
            Data.logEvent(2, "TaskPause", CLASS_NAME);

            // set task as pauzed
            taskPaused = true;

	        // store the previous state
	        previousTaskState = taskState;
			
            // hide everything
            view.setFixation(false);
            view.setCountDown(-1);
            view.setGrid(false);
        }

        // resumes the task
        private void resumeTask() {
            if (view == null)   return;

            // log event task is resumed
            Data.logEvent(2, "TaskResume", CLASS_NAME);

            // show the grid and set the mole
            if (previousTaskState == TaskStates.ColumnSelect || previousTaskState == TaskStates.ColumnSelected) {
			
			    // show the grid and reset the current cue
			    view.setGrid(true);
			    setCueAndState(cueSequence[currentCueIndex]);
		    }
	    
	        // set the previous gamestate
	        setState(previousTaskState);

	        // set task as not longer pauzed
	        taskPaused = false;
        }

        private void destroyView() {

	        // check if a scene thread still exists
	        if (view != null) {

		        // stop the animation thread (stop waits until the thread is finished)
                view.stop();

                // release the thread (For collection)
                view = null;

	        }
        }

        // update dynamic parameter
        public void updateParameter() {

            // if not in dynamic mode or if there no scores to base parameter update on, exit
            if (taskMode != 3 && posAndNegs.Count > 0) return;

            // retrieve last score to base parameter update on
            scoreTypes lastScore = posAndNegs[posAndNegs.Count - 1];

            // check if we need to end task because enough correct responses have been given
            if (dynamicParameter != 5 && stopAfterCorrect != 0) {

                if (lastScore == scoreTypes.TruePositive)                                                   currentCorrect++;
                else if (lastScore == scoreTypes.FalsePositive || lastScore == scoreTypes.FalseNegative)    currentCorrect = 0;

                if (currentCorrect >= stopAfterCorrect) {
                    setState(TaskStates.EndText);
                    logger.Info("Ending task because set amount of correct responses in a row have been reached.");
                    return;
                }
            }

            // for first update, retrieve original parameter value and store local copy (not done for paramter 5, since this already is a local variable)
            // NB. we store local copy becasue we *can* update the local variables in a running filter, but we *cannot* query these, which is needed for subsequent parameter updates. Ath the same time, we *cannot* update the Parameters in a running filter, but we *can* query these.
            if (firstUpdate) {

                // set information on which parameter in which filter will be increased or decreased for which score type
                switch (dynamicParameter) {

                    // dynamic parameter: threshold
                    case 1:
                        filter = "ThresholdClassifier";
                        param = "Thresholds";
                        paramType = "double[][]";
                        increaseType = scoreTypes.FalsePositive;
                        decreaseType = scoreTypes.FalseNegative;
                        addInfo = 2;                                    // column in the parameter matrix holding threshold paramter

                        break;

                    // dynamic parameter: active rate
                    case 2:
                        filter = "ClickTranslator";
                        param = "ActiveRateClickThreshold";
                        paramType = "double";
                        increaseType = scoreTypes.FalsePositive;
                        decreaseType = scoreTypes.FalseNegative;

                        break;

                    // dynamic parameter: active period
                    case 3:
                        filter = "ClickTranslator";
                        param = "ActivePeriod";
                        paramType = "samples";
                        increaseType = scoreTypes.FalsePositive;
                        decreaseType = scoreTypes.FalseNegative;

                        break;

                    // dynamic parameter: mean
                    case 4:
                        filter = "Adaptation";
                        param = "InitialChannelMeans";
                        paramType = "double[]";
                        increaseType = scoreTypes.FalsePositive;
                        decreaseType = scoreTypes.FalseNegative;

                        break;

                    default:
                        if (dynamicParameter != 5) {
                            logger.Error("Non-existing dynamic parameter ID encountered. Check code.");
                            return;
                        }

                        break;
                }

                // if there is a filter to update
                if (filter != "") {

                    // retrieve parameter set from given filter
                    dynamicParameterSet = MainThread.getFilterParametersClone(filter);
                    
                    // retrieve value for given parameter and store local copy    
                    if      (paramType == "double")     localParamCopy = dynamicParameterSet.getValue<double>(param);
                    else if (paramType == "double[]")   localParamCopy = dynamicParameterSet.getValue<double[]>(param);
                    else if (paramType == "double[][]") localParamCopy = dynamicParameterSet.getValue<double[][]>(param);
                    else if (paramType == "samples")    localParamCopy = dynamicParameterSet.getValueInSamples(param);   
                }

                // prevent from retrieving value from parameter set again
                firstUpdate = false;
            }

            // update local copy of parameter and push to filter
            if (localParamCopy != null) {

                // based on parameter type, adjust parameter by increasing or decreasing based on whether the last score was a (true or false) positive or negative
                if (paramType == "double") {
                    if          (lastScore == decreaseType)     localParamCopy = localParamCopy * (1 - (stepSize / 100));
                    else if     (lastScore == increaseType)     localParamCopy = localParamCopy * (1 + (stepSize / 100));
                } 
                else if (paramType == "double[]") {
                    for (int channel = 0; channel < localParamCopy.Length; ++channel) {
                        if      (lastScore == decreaseType) localParamCopy[channel] = localParamCopy[channel] * (1 - (stepSize / 100));
                        else if (lastScore == increaseType) localParamCopy[channel] = localParamCopy[channel] * (1 + (stepSize / 100));
                    }
                } 
                else if (paramType == "double[][]") {
                    for (int channel = 0; channel < localParamCopy[0].Length; ++channel) {                  
                        if      (lastScore == decreaseType) localParamCopy[addInfo][channel] = localParamCopy[addInfo][channel] * (1 - (stepSize / 100));
                        else if (lastScore == increaseType) localParamCopy[addInfo][channel] = localParamCopy[addInfo][channel] * (1 + (stepSize / 100));
                    }
                } 
                else if (paramType == "samples") {
                    if          (lastScore == decreaseType)     localParamCopy = Math.Round(localParamCopy * (1 - (stepSize / 100)));
                    else if     (lastScore == increaseType)     localParamCopy = Math.Round(localParamCopy * (1 + (stepSize / 100)));
                }

                // store adjusted threshold  in dynamic parameter set and re-configure running filter using this adjusted parameter set
                dynamicParameterSet.setValue(param, localParamCopy);
                MainThread.configureRunningFilter(filter, dynamicParameterSet);

                logger.Info("localVar:" + localParamCopy);
            }

            // update dynamic paramter 5 if needed, is done seperately, because it is a local variable
            if (dynamicParameter == 5) {


                logger.Info("ColumnSelectDelay" + columnSelectDelay);

                if      (lastScore == scoreTypes.FalseNegative) columnSelectDelay = (int)Math.Round(columnSelectDelay * (1 + (stepSize / 100)));
                else if (lastScore == scoreTypes.TruePositive)  columnSelectDelay = (int)Math.Round(columnSelectDelay * (1 - (stepSize / 100)));

                logger.Info("ColumnSelectDelay" + columnSelectDelay);
            }
        }

        // update score based on (true and false) positives and negatives and push to view
        private void updateScore() {

            // init
            double tp = 0;
            double fp = 0;
            double fn = 0;

            // cycle through list and count (true and false) positives and negatives
            for(int i=0; i < posAndNegs.Count; i++) {
                if (posAndNegs[i] == scoreTypes.TruePositive || posAndNegs[i] == scoreTypes.TruePositiveEscape) tp++;
                else if (posAndNegs[i] == scoreTypes.FalsePositive) fp++;
                else if (posAndNegs[i] == scoreTypes.FalseNegative || posAndNegs[i] == scoreTypes.FalseNegativeEscape) fn++;
            }

            // calculate score
            if (tp + fp + fn > 0) score = (int)Math.Floor((tp / (tp + fp + fn)) * 100.0);

            // push to view
            view.setScore(posAndNegs, score);
        }

        private void setState(TaskStates state) {

	        // Set state
	        taskState = state;

            logger.Info("set state " + state);

	        switch (state) {

                // starting, pauzed or waiting
                case TaskStates.Wait:
			         
			        // hide text if present
			        view.setText("");

			        // hide the fixation and countdown
			        view.setFixation(false);
                    view.setCountDown(-1);

			        // hide countdown, selection, mole and score
                    view.selectRow(-1, false);
                    view.setGrid(false);

                    // Set wait counter to startdelay
                    if (taskFirstRunStartDelay != 0) {
                        waitCounter = taskFirstRunStartDelay;
                        taskFirstRunStartDelay = 0;
                    } else
			            waitCounter = taskStartDelay;

			        break;

                // countdown when task starts
                case TaskStates.CountDown:

                    // log event countdown is started
                    Data.logEvent(2, "CountdownStarted ", CLASS_NAME);

                    // hide fixation
                    view.setFixation(false);

                    // set countdown
                    if (countdownCounter > 0)
                        view.setCountDown((int)Math.Floor((countdownCounter - 1) / MainThread.getPipelineSamplesPerSecond()) + 1);
                    else
                        view.setCountDown(-1);

                    break;

                // escape cue is being shown
                case TaskStates.EscapeCue:

                    view.selectRow(-1, false);
                    view.selectCell(-1, -1, false);

                    logger.Info("In state escape");

                    Data.logEvent(2, "Escape presented", "");
                    waitCounter = escapeDuration;

                    break;

                // selecting a column
                case TaskStates.ColumnSelect:

                    // get whether current cell contains mole
                    bool containsMole = currentMoleIndex == holeColumns * currentRowID + currentColumnID;

                    // during computer help, create help click vector
                    if (taskMode == 2) {

                        // create empty click vector, length equal to the amount of samples a column is selected, all default to no-click (false)
                        helpClickVector = new List<bool>(new bool[columnSelectDelay]);
                        
                        // temp vars to hold amount of clicks and no-clicks
                        int amountHelpClicks = 0;
                        int amountHelpNoClicks = 0;

                        // determine amount of help clicks and help no-clicks
                        if (containsMole) {
                            amountHelpClicks = (int)Math.Floor(columnSelectDelay * (posHelpPercentage / 100.0));
                            amountHelpNoClicks = columnSelectDelay - amountHelpClicks;
                        } else {
                            amountHelpNoClicks = (int)Math.Floor(columnSelectDelay * (negHelpPercentage / 100.0));
                            amountHelpClicks = columnSelectDelay - amountHelpNoClicks;
                        }

                        logger.Error("helpClicks: " + amountHelpClicks + "helpNoClicks" + amountHelpNoClicks);

                        // adjust the help click vector by inserting the amount of clicks needed 
                        for (int i = Math.Max(0, amountHelpNoClicks); i < columnSelectDelay; i++) helpClickVector[i] = true;

                        // shuffle the vector to create new semi-random, semi-evenly divided vector, or increasingly in the case of containsMole (to prevent the help to click early)
                        if (containsMole)   shuffleHelpVector(true);
                        else                shuffleHelpVector(false);

                        // debug
                        String helpClStr = "";
                        for(int i = 0; i < helpClickVector.Count; i++) {
                            helpClStr = helpClStr + " " + helpClickVector[i].ToString();
                        }

                        logger.Info(helpClStr);
                    }

                    // select cell
                    view.selectCell(currentRowID, currentColumnID, false);

                    // log event that column is highlighted, and whether the column is empty(no mole), blank(no mole and no pile of dirt), or contains a mole
                    if(containsMole)    Data.logEvent(2, "MoleColumn ", currentColumnID.ToString());
                    else                Data.logEvent(2, "EmptyColumn ", currentColumnID.ToString());

                    // set waitcounter
			        waitCounter = columnSelectDelay;

			        break;

                // column was selected
                case TaskStates.ColumnSelected:
			        
			        // select cell and highlight
			        view.selectCell(currentRowID, currentColumnID, true);

                    // log cell click event
                    if (currentMoleIndex == holeColumns * currentRowID + currentColumnID)   Data.logEvent(2, "CellClick", "1");
                    else                                                                    Data.logEvent(2, "CellClick", "0");

                    // set wait time before advancing
                    waitCounter = columnSelectedDelay;

			        break;

                // show end text
                case TaskStates.EndText:
			        
			        // hide hole grid
			        view.setGrid(false);

			        // show text
				    view.setText("Done");

                    // set duration for text to be shown at the end (3s)
                    waitCounter = (int)(MainThread.getPipelineSamplesPerSecond() * 3.0);

                    break;
	        }

        }

        // Stop the task
        private void stopTask() {
            if (view == null)   return;

            // Set state to Wait
            setState(TaskStates.Wait);

            // check if there is no fixed target sequence
	        if (fixedTrialSequence.Length == 0) {

		        // generate new targetlist
		        generateCueSequence();

	        }

        }

        private void generateCueSequence() {

            // create trial sequence array with <numTrials> and temporary lists for moles and escapes
            List<int> molesSequence = new List<int>(new int[numberOfMoles]);
            List<int> escapeSequence = new List<int>(new int[numberOfEscapes]);
            cueSequence = new List<int>(new int[numberOfMoles+numberOfEscapes]);

            // create random moles sequence using minimal and maximal mole distance settings. Sequence contains cell number, ie at which cell the mole will appear
            for (int i = 0; i < numberOfMoles; i++) {
                if (i==0)   molesSequence[i] = rand.Next(1, holeColumns - 1);                                                       // first mole can be placed on any cell, except the first
                else        molesSequence[i] = (molesSequence[i-1] + rand.Next(minMoleDistance, maxMoleDistance)) % holeColumns;    // consecutive moles need to be placed according to minimal and maximal distance to previous mole
            }

            // create random escape cue sequence by placing escapes between moles using minimal interval setting. Sequence contains indices of trialSequence at which escapes will be presented
            int molesLeft = numberOfMoles;                                                  // amount of moles 'left' that can be placed between previous and new escape
            int escapesToPlace = numberOfEscapes;                                           // amount of escapes that still need to be placed between moles 
            int molesNeeded = (escapesToPlace - 1) * escapeInterval;                        // minimum amount of moles needed to place the remaining escapes
            int interval = 0;                                                               // temp variable, holds the distance between consecutive escapes

            // cycle through amount of requested escapes and place each between the moles, ie determine the index (the order) of the escape in the cue sequence
            for (int i = 0; i < numberOfEscapes; i++) {

                if (i == 0) {escapeSequence[i] = rand.Next(0, molesLeft - molesNeeded); }   // first mole can be placed from index 0 to whatever surplus of moles there is
                else {                                                                      // consecutive moles are placed at least the required interval apart, and at most the surplus of moles apart; and not exceeding the length of the cue sequence (ie numberOfMoles + numberOfEscapes)
                    interval = rand.Next(escapeInterval, molesLeft - molesNeeded);
                    escapeSequence[i] = Math.Min(escapeSequence[i-1] + interval + 1, (numberOfMoles + numberOfEscapes)-1);
                }

                // update variables
                escapesToPlace--;
                molesLeft = molesLeft - interval;
                molesNeeded = (escapesToPlace - 1) * escapeInterval;
            }

            // combine moles and escape sequences into cue sequence. First insert -1 at the indices at which escapes are presented,
            for (int i = 0; i < escapeSequence.Count; i++) { cueSequence[escapeSequence[i]] = -1; }

            // then insert cell numbers of moles at indices of cue sequence at which no escape is presented
            int m = 0;                                      // counter for molesequence
            for (int i = 0; i < cueSequence.Count; i++) {
                if (cueSequence[i] == 0) {
                    cueSequence[i] = molesSequence[m];
                    m++;
                }

                logger.Info(cueSequence[i]);

            }
        }

        private void shuffleHelpVector(bool increasing) {

            //
            Random rng = new Random();
            int n = helpClickVector.Count;
            int N = n;

            // loop through elements of list
            while (n > 1) {
                n--;

                // added increasing option: if increasing is true, then chances of skipping the swap decrease linearly, starting with 1, meaning the last element is never swapped. Because the elements are ordered, this means the chances of helping increase linearly
                int s = rng.Next(n, N);
                if (!increasing || s < N - 1) {
                    int k = rng.Next(n + 1);
                    bool value = helpClickVector[k];
                    helpClickVector[k] = helpClickVector[n];
                    helpClickVector[n] = value;
                }
            }
        }

        private void setCueAndState(int index) {

            logger.Info(index);

	        // set mole index to variable
	        currentMoleIndex = index;

	        // hide moles
	        for(int i = 0; i < holes.Count; i++) {
		        if (holes[i].type == MoleCell.CellType.Mole)
			        holes[i].type = MoleCell.CellType.Hole;
	        }

            // if index is -1, place escape, otherwise place mole at given index
            if (currentMoleIndex == -1) {
                view.setEscape(true);
                setState(TaskStates.EscapeCue);
            } else {
                holes[currentMoleIndex].type = MoleCell.CellType.Mole;
                setState(TaskStates.ColumnSelect);
            }
        }


        ////////////////////////////////////////////////
        //  UNP entry points (start, process, stop)
        ////////////////////////////////////////////////

        public void UNP_start(Parameters parentParameters) {
            
            // UNP entry point can only be used if initialized as UNPMenu
            if (!unpMenuTask) {
                logger.Error("Using UNP entry point while the task was not initialized as UNPMenu task, check parameters used to call the task constructor");
                return;
            }


            // create a new parameter object and define this task's parameters
            Parameters newParameters = new Parameters("FollowTask", Parameters.ParamSetTypes.Application);
            defineParameters(ref newParameters);

            // transfer some parameters from the parent
            newParameters.setValue("WindowRedrawFreqMax", parentParameters.getValue<int>("WindowRedrawFreqMax"));
            newParameters.setValue("WindowWidth", parentParameters.getValue<int>("WindowWidth"));
            newParameters.setValue("WindowHeight", parentParameters.getValue<int>("WindowHeight"));
            newParameters.setValue("WindowLeft", parentParameters.getValue<int>("WindowLeft"));
            newParameters.setValue("WindowTop", parentParameters.getValue<int>("WindowTop"));

            // set UNP task standard settings
            inputChannels = 1;
            //allowExit = true;                  // UNPMenu task, allow exit
            newParameters.setValue("WindowBackgroundColor", "0;0;0");
            newParameters.setValue("CountdownTime", "3s");
            newParameters.setValue("TaskInputChannel", 1);
            newParameters.setValue("TaskFirstRunStartDelay", "2s");
            newParameters.setValue("TaskStartDelay", "2s");
            newParameters.setValue("HoleRows", 4);
            newParameters.setValue("HoleColumns", 4);
            newParameters.setValue("RowSelectDelay", 12.0);
            newParameters.setValue("RowSelectedDelay", 5.0);
            newParameters.setValue("ColumnSelectDelay", 12.0);
            newParameters.setValue("ColumnSelectedDelay", 5.0);
            newParameters.setValue("NumberOfTrials", 10);
            newParameters.setValue("TrialSequence", "");

            // get parameter values from app.config
            // cycle through app.config parameter values and try to set the parameter
            var appSettings = System.Configuration.ConfigurationManager.GetSection(CLASS_NAME) as NameValueCollection;
            if (appSettings != null) {
                for (int i = 0; i < appSettings.Count; i++) {

                    // message
                    logger.Info("Setting parameter '" + appSettings.GetKey(i) + "' to value '" + appSettings.Get(i) + "' from app.config.");

                    // set the value
                    newParameters.setValue(appSettings.GetKey(i), appSettings.Get(i));

                }
            }

            // configure task with new parameters
            configure(newParameters);

            // initialize
            initialize();

            // start the task
            start();

            // set the task as running
            unpMenuTaskRunning = true;

        }

        public void UNP_stop() {
            
            // UNP entry point can only be used if initialized as UNPMenu
            if (!unpMenuTask) {
                logger.Error("Using UNP entry point while the task was not initialized as UNPMenu task, check parameters used to call the task constructor");
                return;
            }

            // stop the task from running
            stop();

            // destroy the task
            destroy();

            // flag the task as no longer running (setting this to false is also used to notify the UNPMenu that the task is finished)
            unpMenuTaskRunning = false;

        }

        public bool UNP_isRunning() {
            return unpMenuTaskRunning;
        }

        public void UNP_process(double[] input, bool connectionLost) {

	        // check if the task is running
            if (unpMenuTaskRunning) {

		        // transfer connection lost
		        this.connectionLost = connectionLost;
                
		        // process the input (if the task is not suspended)
		        if (!umpMenuTaskSuspended)		process(input);

	        }

        }

        public void UNP_resume() {

            // lock for thread safety
            lock (lockView) {

                // initialize the view
                initializeView();

            }
	
	        // resume the task
	        resumeTask();

	        // flag task as no longer suspended
	        umpMenuTaskSuspended = false;

        }

        public void UNP_suspend() {

            // flag task as suspended
            umpMenuTaskSuspended = true;

            // pauze the task
            pauseTask();

            // lock for thread safety and destroy the scene
            lock (lockView) {
                destroyView();
            }

        }

    }

}