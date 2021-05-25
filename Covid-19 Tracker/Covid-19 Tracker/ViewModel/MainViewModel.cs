﻿using System;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Timers;
using System.Threading.Tasks;
using System.Windows.Threading;
using Covid_19_Tracker.Base;
using Covid_19_Tracker.Model;
using Microsoft.EntityFrameworkCore;
using ScottPlot;
using ScottPlot.Plottable;
using Serilog;


namespace Covid_19_Tracker.ViewModel
{
    public class MainViewModel : BaseViewModel
    {
        #region Global Variables

        private readonly WpfPlot _plotControl;
        private SignalPlot _mzcrPlot;
        private SignalPlot _whoPlot;
        private ScatterPlot _highlightedPointWho;
        private ScatterPlot _highlightedPointMzcr;
        private Timer _updateTimer;
        private DispatcherTimer _retryTextTimer;
        private int _retrySeconds;
        private string _progressText;
        private bool _progressBar;
        private bool _updateEnabled;
        private bool _uiEnabled;
        private bool _updating;
        private ObservableCollection<Infected> _infected;
        private DateTime _lastUpdate;
        private DateTime _selectedDate;
        private DateTime _earliestDate;
        private DateTime _latestDate;
        private int _mzcrLastHighlightedIndex = -1;
        private int _whoLastHighlightedIndex = -1;
        private double[] _mzcrValues;
        private double[] _whoValues;

        #endregion

        #region Bindable Properties

        public string ProgressText { get => _progressText; private set { _progressText = value; OnPropertyChanged(); } }
        public bool ProgressBar { get => _progressBar; private set { _progressBar = value; OnPropertyChanged(); } }
        public bool UpdateEnabled { get => _updateEnabled; private set { _updateEnabled = value; OnPropertyChanged(); } }
        public bool UiEnabled { get => _uiEnabled; private set { _uiEnabled = value; OnPropertyChanged(); } }
        public ObservableCollection<Infected> Infected { get => _infected; private set { _infected = value; OnPropertyChanged(); } }
        public DateTime SelectedDate { get => _selectedDate; set { _selectedDate = value; OnPropertyChanged(); } }
        public DateTime EarliestDate { get => _earliestDate; set { _earliestDate = value; OnPropertyChanged(); } }
        public DateTime LatestDate { get => _latestDate; set { _latestDate = value; OnPropertyChanged(); } }
        public WpfPlot PlotControl { get => _plotControl; private init { _plotControl = value; OnPropertyChanged(); } }

        #endregion

        #region Command Declarations

        public Command RefreshCommand { get; }
        public Command OnDateChangedCommand { get; }

        #endregion

        #region Command Methods

        /// <summary>
        /// Occcurs once the automatic update timer elapses, or the update button is pressed. Tries to update the database with the most recent data if possible. If there's no internet connection starts a timer that will retry the update every 30s.
        /// </summary>
        private async void UpdateData()
        {
            if (_updating) return;
            _updating = true;
            Log.Information("Starting update.");
            if (await CheckInternetConnection.CheckForInternetConnection(1000))
            {
                _lastUpdate = DateTime.Now;
                await Task.Factory.StartNew(async () =>
                {
                    ProgressText = "Hledám aktualizace...";
                    UpdateEnabled = ProgressBar = true;

                    //Get and save WHO Vaccinations + Country data
                    var listWho = ProcessData.ProcessWhoVaccinated(ApiHandler.DownloadFromUrl("https://covid19.who.int/who-data/vaccination-data.csv").Result).Result;
                    await DataToDb.InitializeCountries(listWho);
                    await DataToDb.SaveToDb(listWho);

                    //Get and save MZČR Summary
                    await DataToDb.SavetoDb(ProcessData.ProcessMzcr(ApiHandler.DownloadFromUrl("https://onemocneni-aktualne.mzcr.cz/api/v2/covid-19/zakladni-prehled.json").Result).Result);

                    //Get and save WHO Infections
                    await DataToDb.SavetoDb(ProcessData.ProcessWhoInfected(ApiHandler.DownloadFromUrl("https://covid19.who.int/WHO-COVID-19-global-data.csv").Result).Result);

                    await UpdateInfectedToDate();
                    await PlotInfectedData();

                    ProgressText = "Poslední aktualizace v " + _lastUpdate.ToString("HH:mm");
                    Log.Information("Update finished.");
                });
            }
            else
            {
                UpdateEnabled = false;
                SetRetryTextTimer();
            }

            ProgressBar = false;
            UiEnabled = true;
            _updating = false;
        }

        /// <summary>
        /// Occurs once the date is changed in the DatePicker. Updates the DataGrid with the data relevant to that date.
        /// </summary>
        private async void OnDateChanged()
        {
            await Task.Factory.StartNew(async () =>
            {
                await UpdateInfectedToDate();
            });
        }

        #endregion

        #region Constructor
        public MainViewModel()
        {
            //Initialize global variables
            SelectedDate = DateTime.Today.AddDays(-1);
            Infected = new ObservableCollection<Infected>();
            //Initialize Plot Controls
            PlotControl = new WpfPlot();
            PlotFactory();
            //Initialize View Commands
            RefreshCommand = new Command(_ => true, _ => UpdateData());
            OnDateChangedCommand = new Command(_ => true, _ => OnDateChanged());
            //Set Progress Text
            ProgressText = "Poslední aktualizace v " + DateTime.Now.ToString("HH:mm");
            //Set Update Timer
            SetUpdateTimer(600000);
            //Call data update
            UpdateData();
        }

        #endregion

        #region Private Methods

        private void PlotFactory()
        {
            using var ctx = new TrackerDbContext();
            _mzcrValues = ctx.Infected.Where(x => x.Source == "mzcr").Select(x => (double)x.TotalCases).Distinct().ToArray();
            _whoValues = ctx.Infected.Where(x => x.Source == "who").Select(x => (double)x.TotalCases).Distinct().ToArray();
            _mzcrPlot = PlotControl.Plot.AddSignal(_mzcrValues, 1, Color.Crimson, label: "MZČR");
            _whoPlot = PlotControl.Plot.AddSignal(_whoValues, 1, Color.DarkTurquoise, "WHO");
            _mzcrPlot.OffsetX = ctx.Infected.MinAsync(r => r.Date).Result.ToOADate();
            _whoPlot.OffsetX = ctx.Infected.MinAsync(r => r.Date).Result.ToOADate();
            PlotControl.MouseDoubleClick += PlotControl_DoubleClick;
            PlotControl.MouseMove += PlotControl_MouseMove;
            PlotControl.Plot.XAxis.DateTimeFormat(true);
            PlotControl.Plot.XLabel("Datum");
            PlotControl.Plot.YLabel("Celkový počet nakažených");
            PlotControl.Plot.Title("Porovnání nakažených v ČR z dat MZČR a WHO");
            PlotControl.Plot.AxisAuto();
            PlotControl.Plot.Legend();
            _highlightedPointMzcr = PlotControl.Plot.AddPoint(0, 0);
            _highlightedPointMzcr.Color = Color.Green;
            _highlightedPointMzcr.MarkerSize = 10;
            _highlightedPointMzcr.MarkerShape = MarkerShape.openCircle;
            _highlightedPointMzcr.IsVisible = false;
            _highlightedPointWho = PlotControl.Plot.AddPoint(0, 0);
            _highlightedPointWho.Color = Color.Green;
            _highlightedPointWho.MarkerSize = 10;
            _highlightedPointWho.MarkerShape = MarkerShape.openCircle;
            _highlightedPointWho.IsVisible = false;
        }

        private async void PlotControl_DoubleClick(object sender, EventArgs e)
        {
            SelectedDate = DateTime.FromOADate(_highlightedPointWho.Xs[0]);
            await UpdateInfectedToDate();
        }

        private void PlotControl_MouseMove(object sender, EventArgs e)
        {
            // determine point nearest the cursor
            var (mouseCoordinateX, _) = PlotControl.GetMouseCoordinates();
            var (mzcrPointX, mzcrPointY, mzcrPointIndex) = _mzcrPlot.GetPointNearestX(mouseCoordinateX);
            var (whoPointX, whoPointY, whoPointIndex) = _whoPlot.GetPointNearestX(mouseCoordinateX);

            // place the highlight over the point of interest
            _highlightedPointMzcr.Xs[0] = mzcrPointX;
            _highlightedPointMzcr.Ys[0] = mzcrPointY;
            _highlightedPointMzcr.IsVisible = true;
            _highlightedPointWho.Xs[0] = whoPointX;
            _highlightedPointWho.Ys[0] = whoPointY;
            _highlightedPointWho.IsVisible = true;

            // render if the highlighted point chnaged
            if (_mzcrLastHighlightedIndex == mzcrPointIndex && _whoLastHighlightedIndex == whoPointIndex) return;
            _mzcrLastHighlightedIndex = mzcrPointIndex;
            _whoLastHighlightedIndex = whoPointIndex;
            PlotControl.Render();
        }

        /// <summary>
        /// Plots data with data from the Infected table in the database
        /// </summary>
        private async Task PlotInfectedData()
        {
            await using var ctx = new TrackerDbContext();
            var casesMzcr = await ctx.Infected.Where(x => x.Source == "mzcr").Select(x => (double)x.TotalCases).Distinct().ToListAsync();
            var casesWho = await ctx.Infected.Where(x => x.Source == "who").Select(x => (double)x.TotalCases).Distinct().ToListAsync();
            Array.Clear(_mzcrValues,0, _mzcrValues.Length);
            Array.Clear(_whoValues, 0, _mzcrValues.Length);
            Array.Copy(casesMzcr.ToArray(), _mzcrValues, casesMzcr.Count);
            Array.Copy(casesWho.ToArray(), _whoValues, casesWho.Count);
            PlotControl.Plot.AxisAuto();
            PlotControl.Plot.Render();
        }

        /// <summary>
        /// Updates the Infected UI from the information in the database.
        /// </summary>
        private async Task UpdateInfectedToDate()
        {
            await using var ctx = new TrackerDbContext();
            Infected = new ObservableCollection<Infected>(await ctx.Infected.Where(x => x.Date.Date == SelectedDate.Date).ToListAsync());
            LatestDate = await ctx.Infected.MaxAsync(r => r.Date);
            EarliestDate = await ctx.Infected.MinAsync(r => r.Date);
        }

        /// <summary>
        /// Timer called every time there's no internet connection. Runs for 30 seconds by default, calls SetRetryTextTimerTick every second.
        /// </summary>
        /// <param name="interval">Time interval determining how often the timer elapses.</param>
        private void SetRetryTextTimer(int interval = 30)
        {
            _retryTextTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            _retryTextTimer.Tick += SetRetryTextTimerTick;
            _retryTextTimer.Start();
            _retrySeconds = interval;
            Log.Warning("Starting retry timer.");
            ProgressText = "Nelze se připojit k internetu, zkouším znovu za " + interval + "s";
        }

        /// <summary>
        /// Tick event called for each 1s tick of SetRetryTextTimer. Calls the update method after the set time elapses.
        /// </summary>
        private void SetRetryTextTimerTick(object sender, EventArgs e)
        {
            _retrySeconds -= 1;
            ProgressText = "Nelze se připojit k internetu, zkouším znovu za " + _retrySeconds + "s";
            if (_retrySeconds != 0) return;
            _retryTextTimer.Stop();
            UpdateData();
            Log.Warning("Retrying update.");
        }

        /// <summary>
        /// Sets a Timer to call the update method every time the timer elapses.
        /// </summary>
        /// <param name="interval">Time interval determining how often the timer elapses.</param>
        private void SetUpdateTimer(double interval)
        {
            _updateTimer = new Timer(interval);
            _updateTimer.Elapsed += UpdateDataEvent;
            _updateTimer.AutoReset = true;
            _updateTimer.Enabled = true;
        }

        /// <summary>
        /// Timed event for SetUpdateTimer, calls the data update method when triggered after the timer elapses.
        /// </summary>
        private void UpdateDataEvent(object sender, ElapsedEventArgs e)
        {
            UpdateData();
        }

        #endregion

        /// <summary>
        /// Notifies the UI that a property has been changed and that it should be updated in the UI.
        /// </summary>
        /// <param name="propertyName">Property to update in the UI.</param>
        private new void OnPropertyChanged([CallerMemberName] string propertyName = "") { base.OnPropertyChanged(propertyName); }
    }
}
