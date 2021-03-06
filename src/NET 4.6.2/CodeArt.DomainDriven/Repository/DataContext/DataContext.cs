﻿using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Text;
using System.Transactions;

using CodeArt.Runtime;
using CodeArt.AppSetting;
using CodeArt.Concurrent;


namespace CodeArt.DomainDriven
{
    public class DataContext : IDataContext, IDisposable
    {
        /// <summary>
        /// 打开数据上文的次数
        /// <para>
        /// 使用此计数,可以嵌套使用数据上下文
        /// </para>
        /// </summary>
        internal int OpenTimes
        {
            get;
            private set;
        }

        private DataContext()
        {
            this.OpenTimes = 0;
            InitializeTransaction();
            InitializeSchedule();
            InitializeRollback();
            InitializeMirror();
        }

        #region 镜像

        private List<IAggregateRoot> _mirrors;
        private bool _lockedMirrors = false;


        private void InitializeMirror()
        {
            _mirrors = new List<IAggregateRoot>();
            _lockedMirrors = false;
        }

        private void DisposeMirror()
        {
            _mirrors.Clear();
            _lockedMirrors = false;
        }

        private void AddMirror(IAggregateRoot obj)
        {
            if (_isCommiting) throw new DataContextException(Strings.CanNotAddMirror);
            if (_mirrors.Exists((t) => { return t.UniqueKey == obj.UniqueKey; })) return;
            _mirrors.Add(obj);
        }

        private void TryAddMirror<T>(QueryLevel level, T root) where T : IAggregateRoot
        {
            if (level == QueryLevel.Mirroring)
            {
                AddMirror(root);
            }
        }

        private void TryAddMirrors<T>(QueryLevel level, IEnumerable<T> objs) where T : IAggregateRoot
        {
            if (level == QueryLevel.Mirroring)
            {
                foreach(var obj in objs)
                {
                    AddMirror(obj);
                }
            }
        }

        private void LockMirrors()
        {
            if (_lockedMirrors) return; //不重复锁定镜像对象
            LockManager.Lock(_mirrors);
            _lockedMirrors = true;
        }

        #endregion

        #region CUD

        public void RegisterAdded<T>(T item, IPersistRepository repository) where T : IAggregateRoot
        {
            ProcessAction(ScheduledAction.Borrow(item, repository, ScheduledActionType.Create));
            item.SaveState();
            item.MarkClean();//无论是延迟执行，还是立即执行，我们都需要提供统一的状态给领域层使用
        }

        public void RegisterUpdated<T>(T item, IPersistRepository repository) where T : IAggregateRoot
        {
            ProcessAction(ScheduledAction.Borrow(item, repository, ScheduledActionType.Update));
            item.SaveState();
            item.MarkClean();//无论是延迟执行，还是立即执行，我们都需要提供统一的状态给领域层使用
        }

        public void RegisterDeleted<T>(T item, IPersistRepository repository) where T : IAggregateRoot
        {
            ProcessAction(ScheduledAction.Borrow(item, repository, ScheduledActionType.Delete));
            item.SaveState();
            item.MarkDirty();//无论是延迟执行，还是立即执行，我们都需要提供统一的状态给领域层使用
        }

        #endregion

        #region 锁

        public void OpenLock(QueryLevel level)
        {
            if (IsLockQuery(level))
                OpenTimelyMode();
        }

        private bool IsLockQuery(QueryLevel level)
        {
            return level == QueryLevel.HoldSingle || level == QueryLevel.Single || level == QueryLevel.Share;
        }

        #endregion

        #region 领域对象查询服务

        /// <summary>
        /// 向数据上下文注册查询，该方法会控制锁和同步查询结果
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="result"></param>
        public T RegisterQueried<T>(QueryLevel level, Func<T> persistQuery) where T : IAggregateRoot
        {
            this.OpenLock(level);
            var result = persistQuery();
            TryAddMirror(level, result);
            return result;
        }

        /// <summary>
        /// 向数据上下文注册查询，该方法会控制锁和同步查询结果
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="result"></param>
        public IEnumerable<T> RegisterQueried<T>(QueryLevel level, Func<IEnumerable<T>> persistQuery) where T : IAggregateRoot
        {
            this.OpenLock(level);
            var result = persistQuery();
            TryAddMirrors(level, result);
            return result;
        }

        /// <summary>
        /// 向数据上下文注册查询，该方法会控制锁和同步查询结果
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="result"></param>
        public Page<T> RegisterQueried<T>(QueryLevel level, Func<Page<T>> persistQuery) where T : IAggregateRoot
        {
            this.OpenLock(level);
            var page = persistQuery();
            var result = page.Objects;
            TryAddMirrors(level, result);
            return page;
        }

        #endregion

        #region 执行计划

        private List<ScheduledAction> _actions;

        private void InitializeSchedule()
        {
            _actions = new List<ScheduledAction>();
        }

        private void DisposeSchedule()
        {
            foreach (var action in _actions)
            {
                ScheduledAction.Return(action);
            }
            _actions.Clear();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="action"></param>
        /// <returns>如果延迟执行，返回true</returns>
        private void ProcessAction(ScheduledAction action)
        {
            if (this._transactionStatus == TransactionStatus.Delay)
            {
                _actions.Add(action);//若处于延迟模式的事务中，那么将该操作暂存
                return;
            }

            if (this._transactionStatus == TransactionStatus.Timely)
            {
                //若已经开启全局事务，直接执行
                ExecuteAction(action);
                return;
            }

            if (this._transactionStatus == TransactionStatus.None)
            {
                //没有开启事务，立即执行
                using (ITransactionManager manager = GetTransactionManager())
                {
                    manager.Begin();
                    ExecuteAction(action);
                    //提交事务
                    RaisePreCommit(action);
                    manager.Commit();
                    RaiseCommitted(action);
                }
                return;
            }
        }



        private void RaisePreCommit(ScheduledAction action)
        {
            switch (action.Type)
            {
                case ScheduledActionType.Create:
                    action.Target.OnAddPreCommit();
                    StatusEvent.Execute(StatusEventType.AddPreCommit, action.Target);
                    break;
                case ScheduledActionType.Update:
                    action.Target.OnUpdatePreCommit();
                    StatusEvent.Execute(StatusEventType.UpdatePreCommit, action.Target);
                    break;
                case ScheduledActionType.Delete:
                    action.Target.OnDeletePreCommit();
                    StatusEvent.Execute(StatusEventType.DeletePreCommit, action.Target);
                    break;
            }
        }

        private void RaiseCommitted(ScheduledAction action)
        {
            switch (action.Type)
            {
                case ScheduledActionType.Create:
                    action.Target.OnAddCommitted();
                    StatusEvent.Execute(StatusEventType.AddCommitted, action.Target);
                    break;
                case ScheduledActionType.Update:
                    action.Target.OnUpdateCommitted();
                    StatusEvent.Execute(StatusEventType.UpdateCommitted, action.Target);
                    break;
                case ScheduledActionType.Delete:
                    action.Target.OnDeleteCommitted();
                    StatusEvent.Execute(StatusEventType.DeleteCommitted, action.Target);
                    break;
            }
        }

        private void RaisePreCommitQueue()
        {
            foreach (var action in _actions)
            {
                RaisePreCommit(action);
            }
        }

        private void RaiseCommittedQueue()
        {
            foreach (var action in _actions)
            {
                RaiseCommitted(action);
            }
        }

        /// <summary>
        /// 检验执行的计划
        /// </summary>
        /// <param name="action"></param>
        private void ValidateAction(ScheduledAction action)
        {
            if (action.Target.IsEmpty())
                throw new ActionTargetIsEmptyException("对象为空，不能执行持久化操作!对象类型：" + action.Target.GetType().FullName);

            if (action.Type == ScheduledActionType.Delete) return; //删除操作，不需要验证固定规则

            ValidationResult result = action.Validate();
            if (!result.IsSatisfied)
                throw new ValidationException(result);
        }

        /// <summary>
        /// 执行计划
        /// </summary>
        /// <param name="action"></param>
        private void ExecuteAction(ScheduledAction action)
        {
            if (action.Expired) return;

            action.Target.LoadState();
            this.ValidateAction(action);

            var repository = action.Repository;
            switch (action.Type)
            {
                case ScheduledActionType.Create:
                    repository.PersistAdd(action.Target);
                    action.Target.MarkClean();
                    break;
                case ScheduledActionType.Update:
                    repository.PersistUpdate(action.Target);
                    action.Target.MarkClean();
                    break;
                case ScheduledActionType.Delete:
                    repository.PersistDelete(action.Target);
                    action.Target.MarkDirty();
                    break;
            }

            action.MarkExpired();
        }

        #endregion

        #region 事务管理

        private TransactionStatus _transactionStatus;
        private int _transactionCount;
        private ITransactionManager _timelyManager;
        /// <summary>
        /// 是否正在执行提交操作
        /// </summary>
        private bool _isCommiting;



        private void InitializeTransaction()
        {
            _transactionStatus = TransactionStatus.None;
            _transactionCount = 0;
            _timelyManager = null;
            _isCommiting = false;
        }

        private void DisposeTransaction()
        {
            _transactionStatus = TransactionStatus.None;
            _transactionCount = 0;
            if (_timelyManager != null)
            {
                _timelyManager.Dispose();
                _timelyManager = null;
            }
            _isCommiting = false;
        }

        public bool IsInTransaction
        {
            get
            {
                return _transactionStatus != TransactionStatus.None;
            }
        }

        /// <summary>
        /// 开启即时事务,并且锁定事务
        /// </summary>
        public void OpenTimelyMode()
        {
            if (_transactionStatus != TransactionStatus.Timely)
            {
                if (!this.IsInTransaction)
                    throw new NotBeginTransactionException(Strings.NotOpenTransaction);

                //开启即时事务
                this._transactionStatus = TransactionStatus.Timely;

                _timelyManager = GetTransactionManager();
                _timelyManager.Begin();

                if (!_isCommiting)
                {
                    //没有之前的队列要执行
                    ExecuteActionQueue();//在提交时更改了事务模式,只有可能是在验证行为时发生，该队列会在稍后立即执行，因此此处不执行队列
                }
            }
        }

        private ITransactionManager GetTransactionManager()
        {
            var factory = TransactionManagerFactory.CreateFactory();
            return factory.CreateManager();
        }

        /// <summary>
        /// 开启事务BeginTransaction和提交事务Commit必须成对出现
        /// </summary>
        public void BeginTransaction()
        {
            if (this.IsInTransaction)
                _transactionCount++;
            else
            {
                _transactionStatus = TransactionStatus.Delay;
                _actions.Clear();
                _transactionCount++;
            }
        }


        public void Commit()
        {
            if (!this.IsInTransaction)
                throw new NotBeginTransactionException(Strings.NotOpenTransaction);
            else
            {
                _transactionCount--;
                if (_transactionCount == 0)
                {
                    if (_isCommiting)
                        throw new RepeatedCommitException(Strings.TransactionRepeatedCommit);

                    _isCommiting = true;

                    try
                    {

                        if (_transactionStatus == TransactionStatus.Delay)
                        {
                            _transactionStatus = TransactionStatus.Timely; //开启即时事务
                            using (ITransactionManager manager = GetTransactionManager())
                            {
                                manager.Begin();
                                ExecuteActionQueue();
                                RaisePreCommitQueue();

                                manager.Commit();

                                RaiseCommittedQueue();
                            }
                        }
                        else if (_transactionStatus == TransactionStatus.Timely)
                        {
                            ExecuteActionQueue();
                            RaisePreCommitQueue();

                            _timelyManager.Commit();

                            RaiseCommittedQueue();
                        }
                    }
                    catch (Exception ex)
                    {
                        throw ex;
                    }
                    finally
                    {
                        Clear();
                        _isCommiting = false;
                    }
                }
            }
        }

        private void ExecuteActionQueue()
        {
            //执行行为队列之前，我们会对镜像进行锁定
            LockMirrors();
            foreach (ScheduledAction action in _actions)
            {
                this.ExecuteAction(action);
            }
        }

        public bool IsDirty
        {
            get
            {
                return _actions.Count > 0;
            }
        }

        /// <summary>
        /// 清理资源
        /// </summary>
        internal void Clear()
        {
            DisposeSchedule();
            DisposeRollback();
            DisposeTransaction();
            DisposeMirror();
        }

        public void Dispose()
        {
            if (this.IsInTransaction)
            {
                this.Rollback();
            }
            else
            {
                Clear();
            }
        }

        #endregion

        #region 回滚

        private RollbackCollection _rollbacks;

        private void InitializeRollback()
        {
            _rollbacks = new RollbackCollection();
        }

        private void DisposeRollback()
        {
            _rollbacks.Clear();
        }

        public void Rollback()
        {
            if (!this.IsInTransaction)
                throw new NotBeginTransactionException(Strings.NotOpenTransaction);
            else
            {
                try
                {
                    _rollbacks.Execute(this);
                }
                catch(Exception ex)
                {
                    throw ex;
                }
                finally
                {
                    Clear();
                }
            }
        }

        public void RegisterRollback(RepositoryRollbackEventArgs e)
        {
            _rollbacks.Add(e);
        }

        #endregion

        #region 基于当前应用程序会话的数据上下文


        private const string _sessionKey = "__DataContext.Current";

        /// <summary>
        /// 获取或设置当前会话的数据上下文
        /// </summary>
        public static DataContext Current
        {
            get
            {
                var dataContext = AppSession.GetOrAddItem<DataContext>(
                    _sessionKey,
                    () => {
                        return Symbiosis.TryMark<DataContext>(_pool, () => { return new DataContext(); });
                    });
                if (dataContext == null) throw new InvalidOperationException("DataContext.Current为null,无法使用仓储对象");
                return dataContext;
            }

            //get
            //{
            //    var current = AppSession.GetItem<DataContext>(_sessionKey);
            //    if (current == null)
            //        throw new DomainDrivenException(Strings.NoDataContext);
            //    return current;
            //}
            //private set
            //{
            //    AppSession.SetItem<DataContext>(_sessionKey, value);
            //}
        }

        private static bool ExistCurrent()
        {
            return AppSession.GetItem<DataContext>(_sessionKey) != null;
        }


        #endregion

        #region 对象池


        private static Pool<DataContext> _pool;


        ///// <summary>
        ///// 打开当前数据上下文
        ///// </summary>
        //public static DataContext Open()
        //{
        //    Symbiosis.Open(); //当数据上下文打开时，共生器也会开启一次
        //    DataContext dataContext;
        //    if (ExistCurrent())
        //    {
        //        dataContext = Current;
        //    }
        //    else
        //    {
        //        dataContext = _pool.Borrow();
        //        Current = dataContext;
        //    }
        //    dataContext.OpenTimes++;
        //    return dataContext;
        //}

        ///// <summary>
        ///// 关闭当前数据上下文
        ///// </summary>
        //public static void Close()
        //{
        //    DataContext dataContext = Current;
        //    dataContext.OpenTimes--;
        //    if (dataContext.OpenTimes == 0)
        //    {
        //        _pool.Return(dataContext);
        //        Current = null;
        //    }
        //    Symbiosis.Close(); //当数据上下文关闭时，共生器也会关闭一次
        //}


        static DataContext()
        {
            _pool = new Pool<DataContext>(() =>
            {
                return new DataContext();
            }, (ctx, phase) =>
            {
                if (phase == PoolItemPhase.Returning)
                {
                    ctx.Clear();
                }
                return true;
            }, new PoolConfig()
            {
                MaxRemainTime = 300 //5分钟之内未被使用，就移除
            });
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 使用事务，在<paramref name="action"/>执行之前会开启一个新的事务并在<paramref name="action"/>执行完毕后结束事务
        /// </summary>
        /// <param name="action"></param>
        internal static void UseTransactionScope(Action action)
        {
            TransactionOptions option = new TransactionOptions();
            option.IsolationLevel = IsolationLevel.ReadUncommitted;
            using (TransactionScope scope = new TransactionScope(TransactionScopeOption.RequiresNew, option))
            {
                action();
                scope.Complete();
            }
        }

        #endregion

    }

    /// <summary>
    /// 事务状态
    /// </summary>
    internal enum TransactionStatus : byte
    {
        /// <summary>
        /// 不开启事务，性能高
        /// </summary>
        None = 1,
        /// <summary>
        /// 延迟事务，只为关键的任务（CUD）开启事务并且这些任务都在最后集中执行
        /// 此模式不能将非持久层的查询操作事务化，只能保证CUD任务是事务性的
        /// </summary>
        Delay = 2,
        /// <summary>
        /// 即时事务，可以保证所有任务在事务范围内，性能最差，但是可以保证应用层的所有任务都在事务之内
        /// </summary>
        Timely = 3
    }


}
