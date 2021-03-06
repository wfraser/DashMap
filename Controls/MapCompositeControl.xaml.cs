﻿using System;
using System.Collections.Generic;
using System.Device.Location;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Maps.Controls;
using Microsoft.Phone.Shell;

namespace Breadcrumbs
{
    public partial class MapCompositeControl : UserControl
    {
        const double MinZoomLevel = 1.0;
        const double MaxZoomLevel = 20.0;

        private static readonly string MapAppID = "d9aef89d-ed06-4025-8364-04ed8caafa73";
        private static readonly string MapAuthToken = "B6_Qh0GhyQZDaCtACpThiQ";

        public MapCompositeControl()
        {
            InitializeComponent();
            m_mapLoaded = false;
            m_centerChangedByCode = false;
            m_haveInitialPosition = false;

            // Handle the lock screen coming up, a phone call being received, etc.
            App.RootFrame.Obscured += RootFrame_Obscured;
            App.RootFrame.Unobscured += RootFrame_Unobscured;
        }

        #region RootFrame Obscured
        void RootFrame_Obscured(object sender, ObscuredEventArgs e)
        {
            // Disable automatic centering on current position.
            // This prevents the map from scrolling and downloading new tiles while obscured.
            if (m_viewModel != null)
            {
                m_oldCenterOnCurrentPosition = m_viewModel.CenterOnCurrentPosition;
                m_viewModel.CenterOnCurrentPosition = false;
            }
        }

        private bool? m_oldCenterOnCurrentPosition;

        void RootFrame_Unobscured(object sender, EventArgs e)
        {
            if (m_oldCenterOnCurrentPosition.HasValue)
            {
                m_viewModel.CenterOnCurrentPosition = m_oldCenterOnCurrentPosition.Value;
                if (m_oldCenterOnCurrentPosition.Value && m_viewModel.MainVM.CurrentPosition != null)
                {
                    // Need to re-center the map.
                    MapControl.Center = m_viewModel.MainVM.CurrentPosition.GeoCoordinate;
                }
            }
        }
        #endregion RootFrame Obscured

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            m_viewModel = (ViewModels.MapCompositeViewModel)DataContext;

            m_viewModel.MainVM.PropertyChanged += MainVM_PropertyChanged;

            if (m_mapLoaded)
                m_viewModel.OverlayText = string.Empty;

            m_viewModel.GetInitialLocation();

            GPX_Changed();
        }

        void ClearTrack()
        {
            while (MapControl.MapElements.Count > 1) // Leave the current position circle.
            {
                MapControl.MapElements.RemoveAt(0);
            }
        }

        private double GetMapPixelSizeMeters()
        {
            // Get the coordinate of the center pixel and the pixel to the right of it.
            Point viewCenter = new Point(MapControl.ActualWidth / 2, MapControl.ActualHeight / 2);

            GeoCoordinate center = MapControl.ConvertViewportPointToGeoCoordinate(viewCenter);
            GeoCoordinate right = MapControl.ConvertViewportPointToGeoCoordinate(new Point(viewCenter.X + 1, viewCenter.Y));

            double pixelRadians = Math.Abs(Utils.ToRadians(center.Longitude) - Utils.ToRadians(right.Longitude));
            return Utils.EarthRadiusMeters * pixelRadians;
        }

        private void UpdateCurrentPositionCircle()
        {
            if (m_viewModel.MainVM.CurrentPosition == null)
            {
                return;
            }

            GeoCoordinate center = m_viewModel.MainVM.CurrentPosition.GeoCoordinate;

            // Minimum radius of 1 pixel.
            double radius = Math.Max(GetMapPixelSizeMeters(), center.HorizontalAccuracy);

            CurrentPositionCircle.Path = Utils.MakeCircle(center, radius);
        }

        void MainVM_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // This is to work around the lack of dependency properties in most of the map controls.
            switch (e.PropertyName)
            {
                case "CurrentGeoCoordinate":
                    {
                        GeoCoordinate center = m_viewModel.MainVM.CurrentPosition.GeoCoordinate;

                        if (m_viewModel.MainVM.IsTrackingEnabled)
                        {
                            // The current position circle is always last.
                            System.Diagnostics.Debug.Assert(MapControl.MapElements.Count > 1);
                            MapPolyline segment = (MapControl.MapElements[MapControl.MapElements.Count - 2] as MapPolyline);
                            if (segment != null)
                            {
                                segment.Path.Add(center);
                            }
                            else
                            {
                                System.Diagnostics.Debug.Assert(false);
                            }
                        }

                        if (m_viewModel.CenterOnCurrentPosition)
                        {
                            m_centerChangedByCode = true;
                            MapControl.Center = center;

                            if (!m_haveInitialPosition)
                            {
                                MapControl.ZoomLevel = 15;
                            }
                        }

                        UpdateCurrentPositionCircle();

                        if (!m_haveInitialPosition)
                        {
                            m_haveInitialPosition = true;
                        }
                    }
                    break;

                case "IsTrackingEnabled":
                    if (!m_viewModel.MainVM.IsTrackingEnabled)
                    {
                        // Tracking disabled; create a new track line layer.
                        AddTrack();
                    }
                    break;

                case "GPX":
                    GPX_Changed();
                    break;
            }
        }

        private void GPX_Changed()
        {
            // Remove all the map elements except the last one (which is the current position circle).
            ClearTrack();

            var points = new List<GeoCoordinate>();

            for (int t = m_viewModel.MainVM.GPX.Tracks.Count - 1; t >= 0; t--)
            {
                GPX.Track track = m_viewModel.MainVM.GPX.Tracks[t];
                for (int s = track.Segments.Count - 1; s >= 0; s--)
                {
                    GPX.TrackSegment segment = track.Segments[s];
                    var segmentPoints = segment.Points.Select(point => point.GeocoordinateEx.GeoCoordinate);
                    points.AddRange(segmentPoints);
                    AddTrack(segmentPoints);
                }
            }

            if (points.Count != 0)
            {
                m_viewModel.CenterOnCurrentPosition = false;
                MapControl.SetView(LocationRectangle.CreateBoundingRectangle(points), new Thickness(25), MapAnimationKind.Parabolic);
            }

            // Add a new empty track, for subsequent points.
            AddTrack();
        }

        private void MapControl_CenterChanged(object sender, MapCenterChangedEventArgs e)
        {
            if (m_centerChangedByCode)
            {
                // You'd expect that the ViewChanging and ViewChanged events would be fired from the Map
                // control when the user pans the view, but you'd be wrong, they only get fired from
                // calls to Map.SetView(), so they are totally worthless.
                //
                // ManipulationStarted/Delta/Completed events don't seem to get fired at all, ever.
                //
                // Instead, we have to handle CenterChanged, which gets fired when the user pans, or when
                // code changes the Center property. To differentiate between the two cases, we maintain
                // m_centerChangedByCode.
                //
                // This API is bad, and Nokia should feel bad.
                // Source: http://social.msdn.microsoft.com/Forums/wpapps/en-US/4e5398de-ec50-46df-84d5-087dcaa20924/wp8-map-viewchanged-and-viewchanging-events-extents

                m_centerChangedByCode = false;
            }
            else
            {
                // Center changed by the user.
                m_viewModel.CenterOnCurrentPosition = false;
            }
        }

        private void ZoomInButton_Click(object sender, RoutedEventArgs e)
        {
            double zoom = MapControl.ZoomLevel;
            zoom += 1.0;
            zoom = Math.Min(Math.Max(zoom, MinZoomLevel), MaxZoomLevel);
            MapControl.ZoomLevel = zoom;

            ZoomOutButton.IsEnabled = true;
            if (zoom == MaxZoomLevel)
            {
                ZoomInButton.IsEnabled = false;
            }
        }

        private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
        {
            double zoom = MapControl.ZoomLevel;
            zoom -= 1.0;
            zoom = Math.Min(Math.Max(zoom, MinZoomLevel), MaxZoomLevel);
            MapControl.ZoomLevel = zoom;

            ZoomInButton.IsEnabled = true;
            if (zoom == MinZoomLevel)
            {
                ZoomOutButton.IsEnabled = false;
            }
        }

        private void RecenterButton_Click(object sender, RoutedEventArgs e)
        {
            m_centerChangedByCode = true;
            if (m_viewModel.MainVM.CurrentPosition != null)
            {
                MapControl.Center = m_viewModel.MainVM.CurrentPosition.GeoCoordinate;
            }
            m_viewModel.CenterOnCurrentPosition = true;
        }

        private void MapControl_Loaded(object sender, RoutedEventArgs e)
        {
            m_mapLoaded = true;
            if (m_viewModel != null)
            {
                m_viewModel.OverlayText = string.Empty;
            }

            // see http://msdn.microsoft.com/library/windowsphone/develop/jj207033(v=vs.105).aspx#BKMK_appidandtoken
            Microsoft.Phone.Maps.MapsSettings.ApplicationContext.ApplicationId = MapAppID;
            Microsoft.Phone.Maps.MapsSettings.ApplicationContext.AuthenticationToken = MapAuthToken;

            // MapPolyline and MapPolygon don't inherit from UIElement, so specifying x:Name
            // on them does nothing (other than creating the empty variables). We need to manually
            // look them up at load time.

            CurrentPositionCircle = MapControl.MapElements.Last() as MapPolygon;

            System.Diagnostics.Debug.Assert(CurrentPositionCircle != null);

            AddTrack();
        }

        private MapPolyline AddTrack(IEnumerable<GeoCoordinate> points = null)
        {
            var line = new MapPolyline();
            line.StrokeColor = Colors.Red;
            line.StrokeThickness = 5.0;

            if (points != null)
            {
                var path = new GeoCoordinateCollection();
                foreach (GeoCoordinate point in points)
                {
                    path.Add(point);
                }
                line.Path = path;
            }

            // Insert the new track segment right behind the current position circle, which is last.
            MapControl.MapElements.Insert(MapControl.MapElements.Count - 1, line);

            return line;
        }

        bool m_centerChangedByCode;
        bool m_mapLoaded;
        private ViewModels.MapCompositeViewModel m_viewModel;
        private bool m_haveInitialPosition;

        private void MapControl_ZoomLevelChanged(object sender, MapZoomLevelChangedEventArgs e)
        {
            UpdateCurrentPositionCircle();
        }
    }
}
