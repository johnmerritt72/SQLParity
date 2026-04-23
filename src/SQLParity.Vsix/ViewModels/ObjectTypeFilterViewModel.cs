using SQLParity.Vsix.Helpers;

namespace SQLParity.Vsix.ViewModels
{
    public class ObjectTypeFilterViewModel : ViewModelBase
    {
        private bool _includeSchemas = true;
        private bool _includeTables = true;
        private bool _includeViews = true;
        private bool _includeStoredProcedures = true;
        private bool _includeFunctions = true;
        private bool _includeSequences = true;
        private bool _includeSynonyms = true;
        private bool _includeUserDefinedDataTypes = true;
        private bool _includeUserDefinedTableTypes = true;

        public bool IncludeSchemas
        {
            get => _includeSchemas;
            set => SetProperty(ref _includeSchemas, value);
        }

        public bool IncludeTables
        {
            get => _includeTables;
            set => SetProperty(ref _includeTables, value);
        }

        public bool IncludeViews
        {
            get => _includeViews;
            set => SetProperty(ref _includeViews, value);
        }

        public bool IncludeStoredProcedures
        {
            get => _includeStoredProcedures;
            set => SetProperty(ref _includeStoredProcedures, value);
        }

        public bool IncludeFunctions
        {
            get => _includeFunctions;
            set => SetProperty(ref _includeFunctions, value);
        }

        public bool IncludeSequences
        {
            get => _includeSequences;
            set => SetProperty(ref _includeSequences, value);
        }

        public bool IncludeSynonyms
        {
            get => _includeSynonyms;
            set => SetProperty(ref _includeSynonyms, value);
        }

        public bool IncludeUserDefinedDataTypes
        {
            get => _includeUserDefinedDataTypes;
            set => SetProperty(ref _includeUserDefinedDataTypes, value);
        }

        public bool IncludeUserDefinedTableTypes
        {
            get => _includeUserDefinedTableTypes;
            set => SetProperty(ref _includeUserDefinedTableTypes, value);
        }

        public RelayCommand SelectAllCommand => new RelayCommand(_ => SetAll(true));
        public RelayCommand ClearAllCommand => new RelayCommand(_ => SetAll(false));

        private void SetAll(bool value)
        {
            IncludeSchemas = value;
            IncludeTables = value;
            IncludeViews = value;
            IncludeStoredProcedures = value;
            IncludeFunctions = value;
            IncludeSequences = value;
            IncludeSynonyms = value;
            IncludeUserDefinedDataTypes = value;
            IncludeUserDefinedTableTypes = value;
        }

        public SQLParity.Core.SchemaReadOptions ToSchemaReadOptions()
        {
            return new SQLParity.Core.SchemaReadOptions
            {
                IncludeSchemas = IncludeSchemas,
                IncludeTables = IncludeTables,
                IncludeViews = IncludeViews,
                IncludeStoredProcedures = IncludeStoredProcedures,
                IncludeFunctions = IncludeFunctions,
                IncludeSequences = IncludeSequences,
                IncludeSynonyms = IncludeSynonyms,
                IncludeUserDefinedDataTypes = IncludeUserDefinedDataTypes,
                IncludeUserDefinedTableTypes = IncludeUserDefinedTableTypes,
            };
        }
    }
}
