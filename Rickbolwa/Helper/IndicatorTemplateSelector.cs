using System;
using System.Collections.Generic;
using System.Text;
using Xamarin.Forms;

namespace Rickbolwa.Helper
{
    public class IndicatorTemplateSelector : DataTemplateSelector
    {
        public DataTemplate ActiveTemplate { get; set; }
        public DataTemplate DefaultTemplate { get; set; }

        protected override DataTemplate OnSelectTemplate(object item, BindableObject container)
        {
            return ((OnboardingSteps)item).IsSelected ? ActiveTemplate : DefaultTemplate;
        }
    }

    public class OnboardingSteps
    {
        public bool IsFinal { get; set; }
        public bool IsSelected { get; set; }
    }