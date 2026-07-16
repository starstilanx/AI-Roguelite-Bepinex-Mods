using System;
using System.IO;
using Newtonsoft.Json;

namespace AIROG_Settlement
{
    // NOTE: Prompt injection intentionally lives in AIROG_GenContext's SettlementProvider,
    // which reads settlement_data.json (see gen_context_guidelines.md). Do not inject here.
    public partial class SettlementPlugin
    {
        public string GetSavePath(string subdir)
        {
            if (string.IsNullOrEmpty(subdir)) return null;
            return Path.Combine(SS.I.saveTopLvlDir, subdir, "settlement_data.json");
        }

        public void SaveSettlementData()
        {
            string subdir = SS.I?.saveSubDirAsArg;
            if (string.IsNullOrEmpty(subdir)) return;

            try {
                string path = GetSavePath(subdir);
                string json = JsonConvert.SerializeObject(CurrentSettlement, Formatting.Indented);
                File.WriteAllText(path, json);
                Log.LogInfo($"Settlement data saved to {subdir}");
            } catch (Exception e) {
                Log.LogError($"Failed to save settlement data: {e.Message}");
            }
        }

        public void LoadSettlementData(string subdir)
        {
            if (string.IsNullOrEmpty(subdir)) return;

            string path = GetSavePath(subdir);
            if (File.Exists(path)) {
                try {
                    string json = File.ReadAllText(path);
                    CurrentSettlement = JsonConvert.DeserializeObject<SettlementState>(json);
                    Log.LogInfo($"Settlement data loaded from {subdir}");
                } catch (Exception e) {
                    Log.LogError($"Failed to load settlement data: {e.Message}");
                    CurrentSettlement = new SettlementState();
                }
            } else {
                CurrentSettlement = new SettlementState();
            }
        }
    }
}
