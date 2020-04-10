﻿// Copyright (c) 2019 .NET Foundation and Contributors. All rights reserved.
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using ReactiveUI;
using Splat;

namespace Sextant.Blazor
{
    /// <summary>
    /// The router specifically designed for use by Sextant.
    /// </summary>
    public class NavigationRouter : ComponentBase, IView, IDisposable, IEnableLogger
    {
        private readonly RouteViewViewModelLocator _routeViewViewModelLocator;
        private readonly UrlParameterViewModelGenerator _urlParameterVmGenerator;
        private IScheduler _mainScheduler;
        private bool _initialized;
        private bool _firstPageRendered;
        private IModalView _modalReference;

        private Dictionary<string, IViewModel> _viewModelDictionary = new Dictionary<string, IViewModel>();

        // Need to mirror the viewmodel stack on IViewStackService.
        private Stack<IViewModel> _mainStack = new Stack<IViewModel>();

        // This will NEVER be accessed by the programmer.  Instead, it only gets filled when hitting the browser back button.
        private Stack<IViewModel> _forwardStack = new Stack<IViewModel>();

        // Mirrored modal stack on IViewStackService.
        private Stack<Type> _modalBackStack = new Stack<Type>();
        private IFullLogger _logger;
        private SextantNavigationManager _navigationManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="NavigationRouter"/> class.
        /// </summary>
        /// <param name="viewStackService">The view stack service.</param>
        /// <param name="navigationManager">The navigation manager.</param>
        /// <param name="routeViewViewModelLocator">The route view model locator.</param>
        /// <param name="urlParameterVmGenerator">The url parameter generator.</param>
        public NavigationRouter(SextantNavigationManager navigationManager, RouteViewViewModelLocator routeViewViewModelLocator, UrlParameterViewModelGenerator urlParameterVmGenerator)
        {
            _navigationManager = navigationManager;
            _routeViewViewModelLocator = routeViewViewModelLocator;
            _urlParameterVmGenerator = urlParameterVmGenerator;

            PagePopped =
                _navigationManager?
                    .LocationChanged
                    .Where(args => args.NavigationType == SextantNavigationType.Popstate)
                    .ObserveOn(MainThreadScheduler)
                    .Select(async args =>
                    {
                        List<IViewModel> viewModels = new List<IViewModel>();
                        string id = null;
                        bool found = false;

                        // If this is false, we're probably pre-rendering.
                        if (_viewModelDictionary.ContainsKey(args.Id))
                        {
                            // This might not be a simple back navigation.  Could be any page in the history.  Need to find this target vm in the stack(s).
                            IViewModel targetViewModel = _viewModelDictionary[args.Id];

                            _logger.Debug($"ViewModel that comes next: {targetViewModel.GetType().Name}");

                            // Assumes pop event is back navigation.
                            foreach (var vm in _mainStack)
                            {
                                if (vm != targetViewModel)
                                {
                                    viewModels.Add(vm);
                                }
                                else
                                {
                                    found = true;
                                    break;
                                }
                            }

                            if (found)
                            {
                                // Stick them in the forward stack now that we know it was a browser back navigation.
                                foreach (var vm in viewModels)
                                {
                                    _forwardStack.Push(vm);
                                    _mainStack.Pop();
                                }

                                return viewModels;
                            }

                            // If we get here, there was **definitely** forward navigation instead!
                            viewModels.Clear();
                            do
                            {
                                var vm = _forwardStack.Peek();
                                if (vm == targetViewModel)
                                {
                                    found = true;
                                }

                                // await _viewStackService.PushPage(_forwardStack.Peek(), null, false);

                                // We're keeping the active viewmodel on the forwardstack so that when we call pushpage, we can recognize it was a forward button nav
                                // and not send the nav command to the internal router.
                            }
                            while (!found && _forwardStack.Count > 0);
                        }

                        return viewModels;
                    })
                    .Concat()
                    .SelectMany(x => x);
        }

        /// <summary>
        /// Gets or sets the content to display when a match is found for the requested route.
        /// </summary>
        [Parameter]
        public RenderFragment<RouteData> Found { get; set; }

        /// <summary>
        /// Gets or sets where the not found content.
        /// </summary>
        [Parameter]
        public RenderFragment NotFound { get; set; }

        /// <summary>
        /// Gets or sets where the views are located.
        /// </summary>
        [Parameter]
        public Assembly AppAssembly { get; set; }

        /// <summary>
        /// Gets or sets the component that displays the modal.
        /// </summary>
        [Parameter]
        public Type ModalComponent { get; set; }

        /// <inheritdoc/>
        public IScheduler MainThreadScheduler => _mainScheduler;

        /// <inheritdoc/>
        public IObservable<IViewModel> PagePopped { get; set; }

        internal IViewModel CurrentViewModel => _mainStack.Count > 0 ? _mainStack.Peek() : null;

        internal object CurrentView { get; set; }

        [Inject]
        private IJSRuntime JSRuntime { get; set; }

        [Inject]
        private NavigationManager BlazorNavigationManager { get; set; }

        /// <inheritdoc/>
        public IObservable<Unit> PopModal()
        {
            // return Observable.Start(
            //     async () =>
            //     {
            //         if (_modalReference == null)
            //         {
            //             throw new Exception("Your modal component type hasn't been defined in SextantRouter.  Make sure it implements IModalView.");
            //         }
            //
            //         if (_modalBackStack.Count > 0)
            //         {
            //             var modalNavigatingAwayFrom = _modalBackStack.Pop();
            //
            //             var modalStack = await _viewStackService.ModalStack.FirstOrDefaultAsync();
            //             if (modalStack.Count > 1 && _modalBackStack.Count > 0)
            //             {
            //                 var previousViewModel = modalStack[modalStack.Count - 2];
            //                 var viewType = _modalBackStack.Peek();
            //
            //                 await _modalReference.ShowViewAsync(viewType, previousViewModel).ConfigureAwait(false);
            //             }
            //             else
            //             {
            //                 await _modalReference.HideAsync().ConfigureAwait(false);
            //             }
            //         }
            //
            //         return Unit.Default;
            //     }).Concat();

            return _navigationManager.GoBackAsync()
        }

        /// <inheritdoc/>
        public IObservable<Unit> PopPage(bool animate = true)
        {
            return Observable.Start(
                async () =>
                {
                    await _navigationManager.GoBackAsync().ConfigureAwait(false);
                    return Unit.Default;
                }).Concat();
        }

        /// <inheritdoc/>
        public IObservable<Unit> PopToRootPage(bool animate = true)
        {
            // Since a popstate event is coming, we're relying on the popped observable to do any stack manipulation.
            // PopRootAndTick should never be called on the ViewStackServiceBase... hence the `IgnoreElements()` call at the end.
            return Observable.Start(
                async () =>
                {
                    var count = _mainStack.Count;

                    await _navigationManager.GoToRootAsync((count - 1) * -1).ConfigureAwait(false);

                    return Unit.Default;
                })
                .Concat()
                .IgnoreElements();
        }

        /// <inheritdoc/>
        public IObservable<Unit> PushModal(IViewModel modalViewModel, string contract, bool withNavigationPage = true)
        {
            return Observable.Start(
                async () =>
                {
                    if (_modalReference == null)
                    {
                        throw new Exception("Your modal component type hasn't been defined in SextantRouter.  Make sure it implements IModalView.");
                    }

                    var viewType = _routeViewViewModelLocator.ResolveViewType(modalViewModel.GetType(), string.IsNullOrWhiteSpace(contract) ? null : contract);

                    if (viewType == null)
                    {
                        throw new Exception($"A view hasn't been registered for the viewmodel type, {modalViewModel.GetType()}, with contract, {contract}.");
                    }

                    // Save the type in a view backstack for later.  Since the StackService doesn't save the contract, we won't know exactly what view to use otherwise.
                    _modalBackStack.Push(viewType);

                    await _modalReference.ShowViewAsync(viewType, modalViewModel).ConfigureAwait(false);

                    return Unit.Default;
                })
                .Concat();
        }

        /// <inheritdoc/>
        public IObservable<Unit> PushPage(IViewModel viewModel, string contract, bool resetStack, bool animate = true)
        {
            return Observable.Start(
                () =>
                {
                    if (resetStack)
                    {
                        // Remove vm from dictionary.
                        while (_mainStack.Count > 0)
                        {
                            var vm = _mainStack.Pop();
                            var pairToDelete = _viewModelDictionary.FirstOrDefault(x => x.Value == vm);
                            if (!pairToDelete.Equals(default(KeyValuePair<string, IViewModel>)))
                            {
                                _viewModelDictionary.Remove(pairToDelete.Key);
                            }
                        }
                    }

                    // Push the vm to the main stack.  While this is for back navigation, it always includes the current viewmodel, too.
                    _mainStack.Push(viewModel);

                    string route = null;

                    // Check if ViewModel is a dummy for a non-registered navigation route ... i.e. controllers, authentication, etc...
                    if (viewModel is IRouteViewModel routeViewModel)
                    {
                        // Set the route from that stored in the dummy viewmodel.
                        route = routeViewModel.Route;
                    }
                    else
                    {
                        // If not, look it up in the RouteViewViewModelLocator
                        route = _routeViewViewModelLocator.ResolveRoute(viewModel.GetType());
                    }

                    // Force all routes to be relative.
                    while (route.StartsWith("/", StringComparison.InvariantCulture))
                    {
                        route = route.Remove(0, 1);
                    }

                    // Check if this is a browser-forward nav... if so, pop the most recent viewmodel.  If not, clear forward stack and proceed normally to navigate internally.
                    if (_forwardStack.Count > 0 && _forwardStack.Peek() == viewModel)
                    {
                        _forwardStack.Pop();
                    }
                    else
                    {
                        while (_forwardStack.Count > 0)
                        {
                            var vm = _forwardStack.Pop();
                            var pairToDelete = _viewModelDictionary.FirstOrDefault(x => x.Value == vm);
                            if (!pairToDelete.Equals(default(KeyValuePair<string, IViewModel>)))
                            {
                                _viewModelDictionary.Remove(pairToDelete.Key);
                            }
                        }

                        // If this is the first page load, then the internal Blazor router has already taken care of navigation. Skip it.
                        if (_firstPageRendered)
                        {
                            // Do a normal navigation.  The second parameter needs to be true to force load a page (i.e. window.location = href) if the path isn't registered with Sextant.
                            // Unfortunately, browser histories can't be cleared.  The user can still navigate to an old page, but those viewmodels will no longer exist.
                            // A new viewmodel will be created if the generator parameter of RegisterBlazorRoute is set to create a viewmodel from a route.
                            BlazorNavigationManager.NavigateTo(_navigationManager.BaseUri + route, viewModel is DirectRouteViewModel);
                        }
                    }

                    // Keep track of the viewmodels generated within the router using a generated key so sextant can restore them on back and forward nav.
                    // First, see if it already exists.
                    var pair = _viewModelDictionary.FirstOrDefault(x => x.Value == viewModel);
                    if (pair.Equals(default(KeyValuePair<string, IViewModel>)))
                    {
                        // That viewmodel doesn't exist yet, so generate a key and store them.
                        pair = new KeyValuePair<string, IViewModel>(Guid.NewGuid().ToString(), viewModel);
                        _viewModelDictionary.Add(pair.Key, pair.Value);
                    }

                    // Place this generated key within the History state so we know what viewmodel to restore on popevents.
                    _ = _navigationManager.ReplaceStateAsync(pair.Key);
                    return Unit.Default;
                },
                MainThreadScheduler);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <inheritdoc />
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _navigationManager?.Dispose();
            }
        }

        /// <inheritdoc/>
        protected override Task OnInitializedAsync()
        {
            return base.OnInitializedAsync();
        }

        /// <inheritdoc/>
        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (!_initialized)
            {
                _initialized = true;
                _navigationManager
                    .LocationChanged
                    .Subscribe(Instance_LocationChanged);

                // Initialize the sextant navigation manager in javascript.
                await _navigationManager.InitializeAsync(JSRuntime).ConfigureAwait(false);

                // Lookup the viewmodel that goes with the url that started the blazor app.
                var results = ParseRelativeUrl(_navigationManager.AbsoluteUri);
                if (results.viewModel == null)
                {
                    // a viewModel wasn't registered for the route... could be a controller on the same server or similar.  Navigate using a generic ViewModel.
                    await _viewStackService.PushPage(new DirectRouteViewModel(_navigationManager.ToBaseRelativePath(_navigationManager.AbsoluteUri)), null, true, false);
                }
                else
                {
                    // Push the viewmodel to the sextant stack.
                    await _viewStackService.PushPage(results.viewModel, results.contract, true, false);

                    // When the blazor app first starts, a page will have already been loaded so it's too late for the page creation logic (in ReactiveRouteView) to set the viewmodel.
                    // We have to do it manually here.
                    ((IViewFor)CurrentView).ViewModel = CurrentViewModel;
                }

                _firstPageRendered = true;
            }
        }

        /// <inheritdoc/>
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            void RenderFragment(RenderTreeBuilder builder2)
            {
                builder2.OpenComponent<Router>(3);
                builder2.AddAttribute(4, nameof(Router.AppAssembly), AppAssembly);
                builder2.AddAttribute(5, nameof(Router.Found), Found);
                builder2.AddAttribute(6, nameof(Router.NotFound), NotFound);
                builder2.CloseComponent();
            }

            builder.OpenComponent<CascadingValue<NavigationRouter>>(0);
            builder.AddAttribute(1, nameof(CascadingValue<NavigationRouter>.Value), this);

            builder.AddAttribute(
                2,
                nameof(CascadingValue<NavigationRouter>.ChildContent),
                (RenderFragment)RenderFragment);
            builder.CloseComponent();

            if (ModalComponent != null)
            {
                builder.OpenComponent(7, ModalComponent);
                builder.AddComponentReferenceCapture(8, componentReference =>
                {
                    if (!(componentReference is IModalView modalView))
                    {
                        throw new Exception($"{nameof(modalView)} does not implement {nameof(IModalView)}. so {nameof(NavigationRouter)} knows how to use it.");
                    }

                    _modalReference = modalView;
                });
                builder.CloseComponent();
            }
        }

        private async void Instance_LocationChanged(NavigationActionEventArgs e)
        {
            if (e == null)
            {
                throw new ArgumentNullException(nameof(e));
            }

            // back or forward event will set history automatically... but link click will not.
            if (e.NavigationType == SextantNavigationType.Url)
            {
                // It was a link click, get a viewmodel for it.
                var results = ParseRelativeUrl(e.Uri, e.Id);

                if (results.viewModel == null)
                {
                    // a viewModel wasn't registered for the route... this a controller or authentication middleware.  Navigate using a generic ViewModel.
                    await _viewStackService.PushPage(new DirectRouteViewModel(_navigationManager.ToBaseRelativePath(e.Uri)), null, true);
                }
                else
                {
                    await _viewStackService.PushPage(results.viewModel, results.contract, false, true);
                }
            }
            else
            {
                // popstate
            }
        }

        private (IViewModel viewModel, string contract) ParseRelativeUrl(string url, string id = null)
        {
            IViewModel viewModel = null;
            Type viewModelType = null;
            char[] separator = new[] { '?' };
            var relativeUri = _navigationManager.ToBaseRelativePath(url);

            var segments = relativeUri.Split(separator, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length; i++)
            {
                segments[i] = Uri.UnescapeDataString(segments[i]);
            }

            // For future, could add interface called IBlazorState where viewmodels are initialized with parameters.
            var parameters = new Dictionary<string, string>();
            if (segments.Length == 2)
            {
                var queryParameters = segments[1].Split(new[] { '&' });
                foreach (var p in queryParameters)
                {
                    var psplit = p.Split(new[] { '=' });
                    parameters.Add(Uri.UnescapeDataString(psplit[0]), Uri.UnescapeDataString(psplit[1]));
                }
            }

            if (segments.Length == 0)
            {
                viewModelType = _routeViewViewModelLocator.ResolveViewModelType("/");
            }
            else
            {
                viewModelType = _routeViewViewModelLocator.ResolveViewModelType("/" + segments[0]);
            }

            if (id == null)
            {
                viewModel = _urlParameterVmGenerator.GetViewModel(viewModelType, parameters);
            }
            else
            {
                // skip modals... shouldn't navigate modals using URL bar
                // var modalStack = await _viewStackService.ModalStack.FirstAsync();
                // var foundViewModel = modalStack.FirstOrDefault(x => x.Id == id);
                // var topModalVM = await _viewStackService.TopPage();
                throw new NotImplementedException();
            }

            return (viewModel, parameters.ContainsKey("contract") ? parameters["contract"] : null);
        }
    }
}
