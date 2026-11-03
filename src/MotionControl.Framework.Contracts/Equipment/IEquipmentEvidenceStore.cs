namespace MotionControl.Framework.Contracts.Equipment
{
    /// <summary>设备项目可选使用的现场证据存储；证据仅供人工核对，不构成自动续跑许可。</summary>
    public interface IEquipmentEvidenceStore
    {
        /// <summary>原子保存设备证据；写入失败必须报告，调用方不得继续需要该证据的动作。</summary>
        void SaveEvidence<T>(string evidenceName, T document) where T : class;
    }

    /// <summary>逐条创建设备记录；同名记录必须拒绝覆盖，创建失败必须报告。</summary>
    public interface IEquipmentRecordStore
    {
        /// <summary>原子创建独立记录，禁止覆盖已有记录。</summary>
        void CreateRecord<T>(string recordName, T document) where T : class;
    }
}
