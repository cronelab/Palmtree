﻿/**
 * The MultiClicksTask class
 * 
 * ...
 * 
 * 
 * Copyright (C) 2022:  RIBS group (Nick Ramsey Lab), University Medical Center Utrecht (The Netherlands) & external contributors
 * Concept:             UNP Team                    (neuroprothese@umcutrecht.nl)
 * Author(s):           Max van den Boom            (info@maxvandenboom.nl)
 *                      Benny van der Vijgh         (benny@vdvijgh.nl)
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

using Palmtree.Applications;
using Palmtree.Core;
using Palmtree.Core.Helpers;
using Palmtree.Core.Params;
using Palmtree.Core.DataIO;
using System.Collections.Specialized;
using Palmtree.Filters;

namespace MultiClicksTask {

    /// <summary>
    /// The <c>MultiClicksTask</c> class.
    /// 
    /// ...
    /// </summary>
    public class MultiClicksTask : IApplication, IApplicationChild {

		private enum TaskStates:int {
			Wait,
			CountDown,
			Task,
			EndText
		};

        private const int CLASS_VERSION = 3;
        private const string CLASS_NAME = "MultiClicksTask";
        private const string CONNECTION_LOST_SOUND = "sounds\\connectionLost.wav";

        private static Logger logger = LogManager.GetLogger(CLASS_NAME);                        // the logger object for the view
        private static Parameters parameters = null;

        private SamplePackageFormat inputFormat = null;
        private MultiClicksView view = null;

        private Random rand = new Random(Guid.NewGuid().GetHashCode());
        private Object lockView = new Object();                                         // threadsafety lock for all event on the view
        private bool taskPauzed = false;								                // flag to hold whether the task is pauzed (view will remain active, e.g. connection lost)

        private bool childApplication = false;								            // flag whether the task is running as a child application (true) or standalone (false)
        private bool childApplicationRunning = false;						            // flag to hold whether the application should be or is running (setting this to false is also used to notify the parent application that the task is finished)
        private bool childApplicationSuspended = false;						                // flag to hold whether the task is suspended (view will be destroyed/re-initiated)

        private bool connectionLost = false;							                // flag to hold whether the connection is lost
        private bool connectionWasLost = false;						                    // flag to hold whether the connection has been lost (should be reset after being re-connected)

        // task input parameters
        private int mWindowLeft = 0;
        private int mWindowTop = 0;
        private int mWindowWidth = 800;
        private int mWindowHeight = 600;
        private int mWindowRedrawFreqMax = 0;
        private RGBColorFloat mWindowBackgroundColor = new RGBColorFloat(0f, 0f, 0f);
        //private bool mWindowed = true;
        //private int mFullscreenMonitor = 0;

        private double mCursorSize = 1f;
        private int mCursorColorRule = 0;
        private RGBColorFloat mCursorColorMiss = new RGBColorFloat(0.8f, 0f, 0f);
        private RGBColorFloat mCursorColorHit = new RGBColorFloat(0.8f, 0.8f, 0f);
        private int mCursorColorHitTime = 0;
        private RGBColorFloat mCursorColorEscape = new RGBColorFloat(0.8f, 0f, 0.8f);
        private int mCursorColorEscapeTime = 0;
        private int mCursorColorTimer = 0;

		private int[] fixedTrialSequence = new int[0];				                    // the fixed trial sequence (input parameter)
		private int mTargetSpeed = 0;
        private List<List<float>> mTargets = new List<List<float>>() {                  // the block/target definitions (1ste dimention are respectively Ys, Heights, Widths; 2nd dimension blocks options) 
            new List<float>(0), 
            new List<float>(0), 
            new List<float>(0),
            new List<float>(0)
        };          
        private List<string> mTargetTextures = new List<string>(0);                     // the block/target texture definitions (each element gives the texture for each block option, corresponds to the 2nd dimension of targets) 
        private int[] mRandomRests = null;                                              // the targets that that can be generated into the target sequence as rest
        private int[][] mRandomTrialCombos = null;                                      // the target combinations that should be generated into the target sequence
        private int[] mRandomTrialQuantities = null;                                    // the amount of respective combination that should be generated into the target sequence

        private int taskInputChannel = 1;											    // input channel
        private int mTaskInputSignalType = 0;										    // input signal type (0 = 0 to 1, 1 = -1 to 1)
        private int mTaskFirstRunStartDelay = 0;                                        // the first run start delay in sample blocks
        private int mTaskStartDelay = 0;									            // the run start delay in sample blocks
        private int mCountdownTime = 0;                                                 // the time the countdown takes in sample blocks
        private bool mShowScore = false;


        // task (active) variables
        private List<int> trialSequence = new List<int>(0);					            // the trial sequence being used in the task (can either be given by input or generated)

        private int waitCounter = 0;
        private int countdownCounter = 0;											    // the countdown timer
        private int hitScore = 0;												        // the score of the cursor hitting a block (in number of samples)
        private bool wasInput = false;                                                  // keep track of previous input

        private TaskStates taskState = TaskStates.Wait;
        private TaskStates previousTaskState = TaskStates.Wait;
        private int currentBlock = MultiClicksView.noBlock;                             // the current block which is in line with X of the cursor (so the middle)
        private int previousBlock = MultiClicksView.noBlock;                            // the previous block that was in line with X of the cursor
        private bool keySequenceState = false;                                          // flag to hold whether the keysequence is active
        private bool keySequencePreviousState = false;


        private float[] storedBlockPositions = null;                                    // to store the previous block positions while suspended

        public MultiClicksTask() : this(false) { }
        public MultiClicksTask(bool childApplication) {

            // transfer the child application flag
            this.childApplication = childApplication;

            // check if the task is standalone (not a child application)
            if (!childApplication) {
            
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

            /*
            parameters.addParameter <int>       (
                "Windowed",
                "Window or Fullscreen - fullscreen is only applied with two monitors",
                "0", "1", "1", new string[] {"Fullscreen", "Window"});

            parameters.addParameter <int>       (
                "FullscreenMonitor",
                "Full screen Monitor",
                "0", "1", "1", new string[] {"Monitor 1", "Monitor 2"});
            */

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

            parameters.addParameter<int>(
                "TaskInputSignalType",
                "Task input signal type",
                "0", "2", "0", new string[] { "Normalizer (0 to 1)", "Normalizer (-1 to 1)", "Constant middle" });

            parameters.addParameter<bool>(
                "TaskShowScore",
                "Show the score",
                "0", "1", "1");

            parameters.addParameter<double>(
                "CursorSize",
                "Cursor size radius in percentage of the screen height",
                "0.0", "50.0", "4.0");

            parameters.addParameter<int>(
                "CursorColorRule",
                "Cursor color rule",
                "0", "2", "0", new string[] { "Hitcolor on target hit (normal)", "Hitcolor on input", "Hitcolor on input - Escape color on escape" });

            parameters.addParameter<RGBColorFloat>(
                "CursorColorMiss",
                "Cursor color when missing",
                "", "", "204;0;0");

            parameters.addParameter<RGBColorFloat>(
                "CursorColorHit",
                "Cursor color when hitting",
                "", "", "204;204;0");

            parameters.addParameter<double>(
                "CursorColorHitTime",
                "Time that the cursor remains in hit color",
                "0", "", "2s");

            parameters.addParameter<RGBColorFloat>(
                "CursorColorEscape",
                "Cursor color when hitting",
                "", "", "170;0;170");

            parameters.addParameter<double>(
                "CursorColorEscapeTime",
                "Time that the cursor remains in escape color",
                "0", "", "2s");

            parameters.addParameter<double[][]>(
                "Targets",
                "Target colors, positions and widths in percentage coordinates\n\nY_perc: The y position of the block on the screen (in percentages of the screen height), note that the value specifies where the middle of the block will be.\nHeight_perc: The height of the block on the screen (in percentages of the screen height)\nWidth_secs: The width of the target block in seconds\nColor: color of the target block as a 24-bit integer (RRGGBB order)",
                "", "", "50,50,50,-100,-100;100,100,100,0,0;2,0.6,0.6,2,3;100,41704,10701220,0,0", new string[] { "Y_perc", "Height_perc", "Width_secs", "color" });

            parameters.addParameter<string[][]>(
                "TargetTextures",
                "Paths of target texture, relative to executable path",
                "", "", "", new string[] { "filepath" });

            parameters.addParameter<double[]>(
                "RandomRests",
                "The rest target(s) that are generated in the target sequence inbetween the trials.\nTargets are specified by the row number as they appear in the Target parameter (zero-based)",
                "0", "", "3 4");

            parameters.addParameter<string[][]>(
                "RandomTrials",
                "The trials that are generated into the target sequence. Each row defines a trial, where each trial is composed of single target or series of targets.\nTargets are specified by the row number as they appear in the Target parameter (zero-based; e.g. double click could be '1 0 1')\nThe quantity column defines how often a trial will occur in the target sequence, as a result, the length of the entire sequence is defined by the RandomTrials and RandomRestTime parameters.",
                "", "", "Click,DblClick;1,2 0 2;2,3", new string[] { "Label", "Trial_combo", "Quantity" });
            
            parameters.addParameter<int>(
                "TargetSpeed",
                "The speed of the targets (in pixels per second)",
                "0", "", "120");

            parameters.addParameter<int[]>(
                "TrialSequence",
                "Fixed sequence in which trials should be presented (leave empty for random)\nNote. indexing is 0 based (so a value of 0 will be the first row from the 'Targets' parameter",
                "0", "", "");

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

        public bool configure(ref SamplePackageFormat input) {
            
            // check sample-major ordered input
            if (input.valueOrder != SamplePackageFormat.ValueOrder.SampleMajor) {
                logger.Error("This application is designed to work only with sample-major ordered input");
                return false;
            }

            // check if the number of input channels is higher than 0
            if (input.numChannels <= 0) {
                logger.Error("Number of input channels cannot be 0");
                return false;
            }

            // store a reference to the input format
            inputFormat = input;
            
            // configure the parameters
            return configure(parameters);

        }

        public bool configure(Parameters newParameters) {
			
            // 
            // TODO: parameters.checkminimum, checkmaximum

            
            // retrieve window settings
            mWindowLeft = newParameters.getValue<int>("WindowLeft");
            mWindowTop = newParameters.getValue<int>("WindowTop");
            mWindowWidth = newParameters.getValue<int>("WindowWidth");
            mWindowHeight = newParameters.getValue<int>("WindowHeight");
            mWindowRedrawFreqMax = newParameters.getValue<int>("WindowRedrawFreqMax");
            mWindowBackgroundColor = newParameters.getValue<RGBColorFloat>("WindowBackgroundColor");
            //mWindowed = true;           // fullscreen not implemented, so always windowed
            //mFullscreenMonitor = 0;     // fullscreen not implemented, default to 0 (does nothing)
            if (mWindowRedrawFreqMax < 0) {
                logger.Error("The maximum window redraw frequency can be no smaller then 0");
                return false;
            }
            if (mWindowWidth < 1) {
                logger.Error("The window width can be no smaller then 1");
                return false;
            }
            if (mWindowHeight < 1) {
                logger.Error("The window height can be no smaller then 1");
                return false;
            }

            // retrieve the input channel setting
            taskInputChannel = newParameters.getValue<int>("TaskInputChannel");
	        if (taskInputChannel < 1) {
		        logger.Error("Invalid input channel, should be higher than 0 (1...n)");
                return false;
	        }
	        if (taskInputChannel > inputFormat.numChannels) {
                logger.Error("Input should come from channel " + taskInputChannel + ", however only " + inputFormat.numChannels + " channels are coming in");
                return false;
	        }

            // retrieve the task delays
            mTaskFirstRunStartDelay = newParameters.getValueInSamples("TaskFirstRunStartDelay");
            mTaskStartDelay = newParameters.getValueInSamples("TaskStartDelay");
            if (mTaskFirstRunStartDelay < 0 || mTaskStartDelay < 0) {
                logger.Error("Start delays cannot be less than 0");
                return false;
            }

            // retrieve the countdown time
            mCountdownTime = newParameters.getValueInSamples("CountdownTime");
            if (mCountdownTime < 0) {
                logger.Error("Countdown time cannot be less than 0");
                return false;
            }

            // retrieve the score parameter
            mShowScore = newParameters.getValue<bool>("TaskShowScore");

            // retrieve the input signal type
            mTaskInputSignalType = newParameters.getValue<int>("TaskInputSignalType");
            
            // retrieve cursor parameters
            mCursorSize = newParameters.getValue<double>("CursorSize");
            mCursorColorRule = newParameters.getValue<int>("CursorColorRule");
            mCursorColorMiss = newParameters.getValue<RGBColorFloat>("CursorColorMiss");
            mCursorColorHit = newParameters.getValue<RGBColorFloat>("CursorColorHit");
            mCursorColorHitTime = newParameters.getValueInSamples("CursorColorHitTime");
            mCursorColorEscape = newParameters.getValue<RGBColorFloat>("CursorColorEscape");
            mCursorColorEscapeTime = newParameters.getValueInSamples("CursorColorEscapeTime");

            // retrieve target settings
            double[][] parTargets = newParameters.getValue<double[][]>("Targets");
            if (parTargets.Length != 4 || parTargets[0].Length < 1) {
                logger.Error("Targets parameter must have at least 1 row and 4 columns (Y_perc, Height_perc, Width_secs, Color)");
                return false;
            }
            
            // TODO: convert mTargets to 3 seperate arrays instead of jagged list?
            mTargets[0] = new List<float>(new float[parTargets[0].Length]);
            mTargets[1] = new List<float>(new float[parTargets[0].Length]);
            mTargets[2] = new List<float>(new float[parTargets[0].Length]);
            mTargets[3] = new List<float>(new float[parTargets[0].Length]);
            for(int row = 0; row < parTargets[0].Length; ++row) {
                mTargets[0][row] = (float)parTargets[0][row];
                mTargets[1][row] = (float)parTargets[1][row];
                mTargets[2][row] = (float)parTargets[2][row];
                if (mTargets[2][row] <= 0) {
                    logger.Error("The value '" + parTargets[2][row] + "' in the Targets parameter is not a valid width value, should be a positive numeric");
                    return false;
                }
                mTargets[3][row] = (float)parTargets[3][row];
                if (mTargets[3][row] < 0 || mTargets[3][row] > 16777215) {
                    logger.Error("The value '" + parTargets[3][row] + "' in the Targets parameter is not a valid color value, should be a numeric value between 0 (including) and 16777215 (including)");
                    return false;
                }
            }
            
            string[][] parTargetTextures = newParameters.getValue<string[][]>("TargetTextures");
            if (parTargetTextures.Length == 0) {
                mTargetTextures = new List<string>(0);
            } else {
                mTargetTextures = new List<string>(new string[parTargetTextures[0].Length]);
                for (int row = 0; row < parTargetTextures[0].Length; ++row) mTargetTextures[row] = parTargetTextures[0][row];
            }

            // retrieve the target speed
            mTargetSpeed = newParameters.getValue<int>("TargetSpeed");
            if (mTargetSpeed < 1) {
                logger.Error("The TargetSpeed parameter be at least 1");
                return false;
            }

            // retrieve the number of trials and (fixed) trial sequence
            fixedTrialSequence = newParameters.getValue<int[]>("TrialSequence");
            if (fixedTrialSequence.Length == 0) {
                // no fixed sequence

                // retrieve randomRestTime settings
                mRandomRests = newParameters.getValueInSamples<int[]>("RandomRests");
                if (mRandomRests.Length == 0) {
                    logger.Error("At least one random rest target should be given in order to generate a random trial sequence");
                    return false;
                }

                // retrieve random trials settings
                string[][] parRandomTrials = newParameters.getValue<string[][]>("RandomTrials");
                if (parRandomTrials.Length != 3 || parRandomTrials[0].Length == 0) {
                    logger.Error("RandomTrials parameter must have 3 columns (Label, Trial_combo, Quantity) and at least one row in order to generate a random trial sequence");
                    return false;
                }
				
                mRandomTrialCombos = new int[parRandomTrials[0].Length][];
                mRandomTrialQuantities = new int[parRandomTrials[0].Length];
                int totalQuantity = 0;
                for (int i = 0; i < parRandomTrials[0].Length; i++) {
                    string[] trTargets = parRandomTrials[1][i].Split(Parameters.ArrDelimiters, StringSplitOptions.RemoveEmptyEntries);
                    if (trTargets.Length == 0) {
                        logger.Error("Invalid or empty Trial_combo '" + parRandomTrials[1][i] + "'");
                        return false;
                    }

                    mRandomTrialCombos[i] = new int[trTargets.Length];
                    for (int j = 0; j < trTargets.Length; j++) {
                        int targetValue = 0;
                        if (!int.TryParse(trTargets[j], out targetValue)) {
                            logger.Error("Cannot interpret all values in the Trial_combo '" + parRandomTrials[1][i] + "', invalid targets");
                            return false;
                        }
                        if (targetValue < 0 || targetValue >= mTargets[0].Count) {
                            logger.Error("The value '" + targetValue + "' in Trial_combo '" + parRandomTrials[1][i] + "' is an invalid target index");
                            return false;
                        }
                        mRandomTrialCombos[i][j] = targetValue;
                    }

                    int targetQuantity = 0;
                    if (!int.TryParse(parRandomTrials[2][i], out targetQuantity) || targetQuantity < 0) {
                        logger.Error("Cannot interpret the quantity value '" + parRandomTrials[2][i] + "', should be a positive numeric value");
                        return false;
                    }
                    mRandomTrialQuantities[i] = targetQuantity;
                    totalQuantity += targetQuantity;
                }
                if (totalQuantity == 0) {
                    logger.Error("The total quantity of all trials cannot be 0, specify at least one row with a quantity above 0");
                    return false;
                }

            } else {
                // fixed sequence

                // loop through the targets in the sequence
                for (int i = 0; i < fixedTrialSequence.Length; ++i) {
                    
                    if (fixedTrialSequence[i] < 0) {
                        logger.Error("The TrialSequence parameter contains a target index (" + fixedTrialSequence[i] + ") that is below zero, check the TrialSequence");
                        return false;
                    }
                    if (fixedTrialSequence[i] >= mTargets[0].Count) {
                        logger.Error("The TrialSequence parameter contains a target index (" + fixedTrialSequence[i] + ") that is out of range, check the Targets parameter. (note that the indexing is 0 based)");
                        return false;
                    }
                }

            }

            // retun success
            return true;

        }
		
        public bool initialize() {
                        
            // lock for thread safety
            lock(lockView) {

                // check the view (thread) already exists, stop and clear the old one.
                destroyView();

                // initialize the view
                initializeView();

                // check if a target sequence is set
                if (fixedTrialSequence.Length == 0) {
		            // fixed sequence not set in parameters, generate
		
		            // Generate trial sequence
		            generateTrialSequence();

	            } else {
		            // fixed sequence is set in parameters

		            // clear the trials
		            if (trialSequence.Count != 0)		trialSequence.Clear();
                
		            // transfer the fixed trial sequence
                    trialSequence = new List<int>(fixedTrialSequence);

	            }
	        
	            // initialize the trial sequence
	            view.initBlockSequence(trialSequence, mTargets);

            }

            // return success
            return true;

        }

        private void initializeView() {

            // create the view
            view = new MultiClicksView(mWindowRedrawFreqMax, mWindowLeft, mWindowTop, mWindowWidth, mWindowHeight, false);
            view.setBackgroundColor(mWindowBackgroundColor.getRed(), mWindowBackgroundColor.getGreen(), mWindowBackgroundColor.getBlue());

            // set task specific display attributes 
            view.setBlockSpeed(mTargetSpeed);                                   // target speed
            view.setCursorSizePerc(mCursorSize);                                // cursor size radius in percentage of the screen height
            view.setCursorHitColor(mCursorColorHit);                            // cursor hit color
            view.setCursorMissColor(mCursorColorMiss);                          // cursor out color            
            view.initBlockTextures(mTargetTextures);                            // initialize target textures (do this before the thread start)
            view.centerCursor();                                                // set the cursor to the middle of the screen
            view.setFixation(false);                                            // hide the fixation
            view.setCountDown(-1);                                              // hide the countdown

            // check if the cursor rule is set to hitcolor on hit, if so
            // then make the color automatically determined in the Scenethread by it's variable 'mCursorInCurrentBlock',
            // this makes the color update quickly, since the scenethread is executed at a higher frequency
            if (mCursorColorRule == 0) {
                view.setCursorColorSetting(3);
            }

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

            // check if the task is standalone (not a child application)
            if (!childApplication) {

                // store the generated sequence in the output parameter xml
                Data.adjustXML(CLASS_NAME, "TrialSequence", string.Join(" ", trialSequence));

            }

            // lock for thread safety
            lock (lockView) {

                if (view == null)   return;

                // log event task is started
                Data.logEvent(2, "TaskStart", CLASS_NAME);

                // reset the score
                hitScore = 0;

                // reset countdown to the countdown time
                countdownCounter = mCountdownTime;

                if (mTaskStartDelay != 0 || mTaskFirstRunStartDelay != 0) {
		            // wait

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

            // process
            int totalSamples = inputFormat.numSamples * inputFormat.numChannels;
            for (int sample = 0; sample < totalSamples; sample += inputFormat.numChannels)
                process(sample + input[taskInputChannel - 1]);
            
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
                        // just discovered it was lost

                        // set the connection as was lost (this also will make sure the lines in this block willl only run once)
                        connectionWasLost = true;

                        // pauze the task
                        pauzeTask();

			            // show the lost connection warning
			            view.setConnectionLost(true);

                        // play the connection lost sound continuously every 2 seconds
                        SoundHelper.playContinuousAtInterval(CONNECTION_LOST_SOUND, 2000);

                    }

                    // do not process any further
                    return;

                } else if (connectionWasLost && !connectionLost) {
                    // if the connection was lost and is not lost anymore

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
	            if (taskPauzed)		    return;
                
	            // use the task state
	            switch (taskState) {

		            case TaskStates.Wait:
			            // starting, pauzed or waiting
			
			            if(waitCounter == 0) {

				            // set the state to countdown
				            setState(TaskStates.CountDown);

			            } else
				            waitCounter--;

			            break;

		            case TaskStates.CountDown:
			            // Countdown before start of task
			
			            // check if the task is counting down
			            if (countdownCounter > 0) {

				            // still counting down

                            // display the countdown
                            view.setCountDown((int)Math.Floor((countdownCounter - 1) / MainThread.getPipelineSamplesPerSecond()) + 1);

                            // reduce the countdown timer
                            countdownCounter--;

                        } else {
                            // done counting down

                            // hide the countdown counter
                            view.setCountDown(-1);

                            // set the current block to no block
                            currentBlock = MultiClicksView.noBlock;

				            // reset the score
				            hitScore = 0;

				            // set the state to task
				            setState(TaskStates.Task);

			            }

			            break;

		            case TaskStates.Task:

			            // check the input type
			            if (mTaskInputSignalType == 0) {
				            // Normalizer (0 to 1)
                            
				            view.setCursorNormY(input);	// setCursorNormY will take care of values below 0 or above 1)
		
			            } else if (mTaskInputSignalType == 1) {
				            // Normalizer (-1 to 1)

				            view.setCursorNormY((input + 1.0) / 2.0);

			            } else if (mTaskInputSignalType == 2) {
				            // Constant middle

				            view.setCursorNormY(0.5);

			            }

			            // check if it is the end of the task
			            if (currentBlock == trialSequence.Count - 1 && (view.getCurrentBlock() == MultiClicksView.noBlock)) {
				            // end of the task

				            setState(TaskStates.EndText);

			            } else {
				            // not the end of the task

				            // check if the color is based on input
				            if (mCursorColorRule == 1 || mCursorColorRule == 2) {
					            // 1. Hitcolor on input or 
					            // 2. Hitcolor on input - Escape color on escape

					            // check if there is time on the timer left
					            if (mCursorColorTimer > 0) {

						            // count back the timer
						            mCursorColorTimer--;

						            // set the color back to miss if the timer is finished
						            if (mCursorColorTimer == 0)
							            view.setCursorColorSetting(0);

					            }


                                // log if current clickstate has changed
                                if (wasInput != (input == 1)) {
                                    Data.logEvent(2, "ClickChange", (input == 1) ? "1" : "0");
                                    wasInput = (input == 1);
                                }

                                // check the color rule
                                if (mCursorColorRule == 2) {
                                    // 2. Hitcolor on input - Escape color on escape

                                    // check whether the task is independent (i.e. not a child application)
                                    // note: when the taskparent has a parent application, that application will use the key-sequence
                                    if (!childApplication) {

                                        // check if the escape-state has changed
                                        keySequenceState = Globals.getValue<bool>("KeySequenceActive");
                                        if (keySequenceState != keySequencePreviousState) {

                                            // log and update
                                            Data.logEvent(2, "EscapeChange", (keySequenceState) ? "1" : "0");
                                            keySequencePreviousState = keySequenceState;

                                            // starts the full refractory period on all translated channels
                                            MainThread.configureRunningFilter("ClickTranslator", null, (int)ClickTranslatorFilter.ResetOptions.StartFullRefractoryPeriod);

                                        }

                                        // check if a keysequence input comes in or a click input comes in
                                        if (keySequenceState) {

                                            // set the color
                                            view.setCursorColorSetting(2);

							                // set the timer
							                if (mCursorColorEscapeTime == 0)	mCursorColorTimer = 1;
							                else								mCursorColorTimer = mCursorColorEscapeTime;

						                } else {

                                            // check if a click was made
                                            if (input == 1) {

                                                // set the color
                                                view.setCursorColorSetting(1);

                                                // set the timer
                                                if (mCursorColorHitTime == 0)	mCursorColorTimer = 1;
								                else							mCursorColorTimer = mCursorColorHitTime;

							                }

						                }
                                        
                                    }

					            } else {
                                    // 1. Hitcolor on input

                                    // check if a click was made
                                    if (input == 1) {
						
							            // set the color
							            view.setCursorColorSetting(1);

							            // set the timer
							            if (mCursorColorHitTime == 0)   mCursorColorTimer = 1;
							            else							mCursorColorTimer = mCursorColorHitTime;

						            }
					            }

				            }

				            // retrieve the current block and if cursor is in this block
				            currentBlock = view.getCurrentBlock();
                            bool mIsCursorInCurrentBlock = view.getCursorInCurrentBlock();

                            // retrieve which block condition the current block is
                            int blockCondition = -1;
                            if (currentBlock != MultiClicksView.noBlock) blockCondition = trialSequence[currentBlock];

                            // log event if the current block has changed and update the previous block placeholder
                            if (currentBlock != previousBlock)     Data.logEvent(2, "Changeblock", (currentBlock.ToString() + ";" + blockCondition.ToString()));
                            previousBlock = currentBlock;
                            
                            // add to score if cursor hits the block
                            if (mIsCursorInCurrentBlock) hitScore++;

				            // update the score for display
				            if (mShowScore)     view.setScore(hitScore);

			            }

			            break;

		            case TaskStates.EndText:
			            // end text

			            if(waitCounter == 0) {

                            // log event task is stopped
                            Data.logEvent(2, "TaskStop", CLASS_NAME + ";end");

                            // stop the task
                            // this will also call stop(), and as a result stopTask()
                            if (childApplication)        AppChild_stop();
                            else                    MainThread.stop(false);

			            } else
				            waitCounter--;

			            break;
	            }

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


        private void destroyView() {

	        // check if a scene thread still exists
	        if (view != null) {

		        // stop the animation thread (stop waits until the thread is finished)
                view.stop();

                // release the thread (For collection)
                view = null;

	        }

        }

        // pauzes the task
        private void pauzeTask() {
            if (view == null)   return;

            // log event task is paused
            Data.logEvent(2, "TaskPause", CLASS_NAME);

            // set task as pauzed
            taskPauzed = true;

	        // store the previous state
	        previousTaskState = taskState;
	
	        // store the block positions
	        if (previousTaskState == TaskStates.Task) {
                storedBlockPositions = view.getBlockPositions();
	        }

		    // hide everything
		    view.setFixation(false);
		    view.setCountDown(-1);
		    view.setBlocksVisible(false);
		    view.setCursorVisible(false);
		    view.setBlocksMove(false);
		    view.setScore(-1);

        }

        // resumes the task
        private void resumeTask() {
            if (view == null)   return;

            // log event task is paused
            Data.logEvent(2, "TaskResume", CLASS_NAME);

            // re-instate the block positions
            if (previousTaskState == TaskStates.Task) {
                view.setBlockPositions(storedBlockPositions);
	        }

            // set the previous gamestate
	        setState(previousTaskState);

	        // set task as not longer pauzed
	        taskPauzed = false;

        }


        private void setState(TaskStates state) {
            
	        // Set state
	        taskState = state;

            switch (state) {
		        case TaskStates.Wait:
			        // starting, pauzed or waiting

                    // hide text if present
                    view.setText("");

				    // hide the fixation and countdown
				    view.setFixation(false);
                    view.setCountDown(-1);

				    // stop the blocks from moving
				    view.setBlocksMove(false);

				    // hide the countdown, blocks, cursor and score
				    view.setBlocksVisible(false);
				    view.setCursorVisible(false);
				    view.setScore(-1);

                    // Set wait counter to startdelay
                    if (mTaskFirstRunStartDelay != 0) {
                        waitCounter = mTaskFirstRunStartDelay;
                        mTaskFirstRunStartDelay = 0;
                    } else
			            waitCounter = mTaskStartDelay;

			        break;

		        case TaskStates.CountDown:
                    // countdown when task starts

                    // log event countdown is started
                    Data.logEvent(2, "CountdownStarted ", "");

                    // hide text if present
                    view.setText("");

				    // hide fixation
				    view.setFixation(false);

				    // set countdown
                    if (countdownCounter > 0)
                        view.setCountDown((int)Math.Floor((countdownCounter - 1) / MainThread.getPipelineSamplesPerSecond()) + 1);
                    else
                        view.setCountDown(-1);

			        break;


		        case TaskStates.Task:
                    // perform the task

                    // log event countdown is started
                    Data.logEvent(2, "TrialStart ", "");

                    /*
				    // hide text if present
				    view->setText("");
                    */

                    // hide the countdown counter
                    view.setCountDown(-1);

				    // set the score for display
				    if (mShowScore)		view.setScore(hitScore);

				    // reset the cursor position
				    view.centerCursor();

				    // show the cursor
				    view.setCursorVisible(true);

				    // show the blocks and start the blocks animation
				    view.setBlocksVisible(true);
				    view.setBlocksMove(true);

			        break;

		        case TaskStates.EndText:
			        // show text
			
				    // stop the blocks from moving
				    view.setBlocksMove(false);

				    // hide the blocks and cursor
				    view.setBlocksVisible(false);
				    view.setCursorVisible(false);
                    
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

            // set the current block to no block
            currentBlock = MultiClicksView.noBlock;

            // set state to wait
            setState(TaskStates.Wait);
    
            // initialize the target sequence already for a possible next run
	        if (fixedTrialSequence.Length == 0) {

		        // Generate targetlist
		        generateTrialSequence();

	        }

            // initialize the target sequence
	        view.initBlockSequence(trialSequence, mTargets);

        }


        private void generateTrialSequence() {
	        
	        // clear the targets
	        if (trialSequence.Count != 0)		trialSequence.Clear();

            // count the number of trials
            int totalQuantity = 0;
            for (int i = 0; i < mRandomTrialQuantities.Length; i++) {
                totalQuantity += mRandomTrialQuantities[i];
            }

            // count the number of rests (= number of trials - 1, but + 1 to have an additional rest in the end after all the trials)
            int totalRests = totalQuantity;

            // create an array with the rests to be used inbetween the trials and after the last trial
            int[] arrRestIndices = new int[totalRests];
            int restCounter = 0;
            for (int i = 0; i < arrRestIndices.Length; i++) {
                arrRestIndices[i] = restCounter;
                restCounter++;
                if (restCounter == mRandomRests.Length) restCounter = 0;
            }
            arrRestIndices.Shuffle();

            // create an array the size of the number of trials and fill it with the indices of the trial-combination that should be in there (given the quantity each trial-combination
            int[] arrTrialIndices = new int[totalQuantity];
            int counter = 0;
            for (int i = 0; i < mRandomTrialQuantities.Length; i++) {
                for (int j = 0; j < mRandomTrialQuantities[i]; j++) {
                    arrTrialIndices[counter] = i;
                    counter++;
                }
            }

            // shuffle the array to make the trials random
            arrTrialIndices.Shuffle();

            // loop throug the trials that should be added
            for (int i = 0; i < arrTrialIndices.Length; i++) {

                // add the combination to the target sequence
                for (int j = 0; j < mRandomTrialCombos[arrTrialIndices[i]].Length; j++) {
                    trialSequence.Add(mRandomTrialCombos[arrTrialIndices[i]][j]);
                }

                // check if this is not the last item
                if (i != arrTrialIndices.Length - 1) {

                    // add a random rest (inbetween the trials)
                    trialSequence.Add(mRandomRests[arrRestIndices[i]]);
                    
                }

            }

            // add a rest after all the trials
            trialSequence.Add(mRandomRests[arrRestIndices[arrRestIndices.Length - 1]]);


        }


        ////////////////////////////////////////////////
        //  Child application entry points (start, process, stop)
        ////////////////////////////////////////////////

        public void AppChild_start(Parameters parentParameters) {

            // entry point can only be used if initialized as child application
            if (!childApplication) {
                logger.Error("Using child entry point while the task was not initialized as child application task, check parameters used to call the task constructor");
                return;
            }

            // create a new parameter object and define this task's parameters
            Parameters newParameters = new Parameters(CLASS_NAME + "_child", Parameters.ParamSetTypes.Application);
            defineParameters(ref newParameters);

            // transfer some parameters from the parent
            newParameters.setValue("WindowRedrawFreqMax", parentParameters.getValue<int>("WindowRedrawFreqMax"));
            newParameters.setValue("WindowWidth", parentParameters.getValue<int>("WindowWidth"));
            newParameters.setValue("WindowHeight", parentParameters.getValue<int>("WindowHeight"));
            newParameters.setValue("WindowLeft", parentParameters.getValue<int>("WindowLeft"));
            newParameters.setValue("WindowTop", parentParameters.getValue<int>("WindowTop"));

            // set child task standard settings
            inputFormat.numChannels = 1;
            newParameters.setValue("WindowBackgroundColor", "0;0;0");
            newParameters.setValue("CountdownTime", "3s");
            newParameters.setValue("TaskShowScore", true);
            newParameters.setValue("TaskInputSignalType", 1);
            newParameters.setValue("TaskInputChannel", 1);
            newParameters.setValue("TaskFirstRunStartDelay", "2s");
            newParameters.setValue("TaskStartDelay", "2s");
            newParameters.setValue("CursorSize", 4.0);
            newParameters.setValue("CursorColorRule", 0);
            newParameters.setValue("CursorColorMiss", "204;0;0");
            newParameters.setValue("CursorColorHit", "204;204;0");
            newParameters.setValue("CursorColorHitTime", 0.0);
            newParameters.setValue("CursorColorEscape", "170;0;170");
            newParameters.setValue("CursorColorEscapeTime", 0.0);
            newParameters.setValue("TargetSpeed", 120);
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
            childApplicationRunning = true;

        }

        public void AppChild_stop() {
            
            // entry point can only be used if initialized as child application
            if (!childApplication) {
                logger.Error("Using child entry point while the task was not initialized as child application task, check parameters used to call the task constructor");
                return;
            }

            // stop the task from running
            stop();

            // destroy the task
            destroy();

            // flag the task as no longer running (setting this to false is also used to notify the UNPMenu that the task is finished)
            childApplicationRunning = false;

        }

        public bool AppChild_isRunning() {
            return childApplicationRunning;
        }

        public void AppChild_process(double[] input, bool connectionLost) {

	        // check if the task is running
	        if (childApplicationRunning) {

		        // transfer connection lost
		        this.connectionLost = connectionLost;

		        // process the input
		        if (!childApplicationSuspended)		process(input);

	        }

        }

        public void AppChild_resume() {

            // lock for thread safety
            lock(lockView) {

                // initialize the view
                initializeView();
                
                // (re-) initialize the block sequence
		        view.initBlockSequence(trialSequence, mTargets);
                
            }

	        // resume the task
	        resumeTask();

	        // flag task as no longer suspended
	        childApplicationSuspended = false;

        }

        public void AppChild_suspend() {

            // flag task as suspended
            childApplicationSuspended = true;

            // pauze the task
            pauzeTask();

            // lock for thread safety and destroy the scene
            lock (lockView) {
                destroyView();
            }

        }

    }

}
