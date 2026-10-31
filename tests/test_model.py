import copy,json,struct,threading
from concurrent.futures import ThreadPoolExecutor
from fixtures import Fixture
from studio.model import Store,StudioError,digest,read,split_prompt,timing

class ProjectTests(Fixture):
    def test_edit_creates_immutable_history_and_survives_restart_without_touching_sources(self):
        before=self.current();expected=before['revision']
        result=self.save({'title':'Новая постановка','settings':{**self.block()['settings'],'seed':str(2**64-1)}})
        self.assertEqual(expected+1,result['revision']);self.assertEqual('Новая постановка',self.block(result)['title'])
        history=self.store.history(self.pid);snapshot=read(self.store.location(self.pid).parent/'History'/history[0]['file'])
        self.assertEqual(expected,snapshot['revision']);self.assertEqual('Исходный блок',self.block(snapshot)['title'])
        fresh=Store(self.book,self.amv,self.base/'state').get(self.pid)
        self.assertEqual(str(2**64-1),self.block(fresh)['settings']['seed']);self.assertEqual(self.plan_digest,digest(self.plan))

    def test_stale_revision_rejected_without_extra_history_or_lost_winner(self):
        p=self.current();self.save({'notes':'Правка автора'});count=len(self.store.history(self.pid))
        with self.assertRaises(StudioError) as caught:self.store.save_block(self.pid,self.bid,{'notes':'Устаревший агент'},p['revision'],'agent')
        self.assertEqual(409,caught.exception.status);self.assertEqual('Правка автора',self.block()['notes'])
        self.assertEqual(count,len(self.store.history(self.pid)))

    def test_concurrent_writes_at_same_revision_have_exactly_one_winner(self):
        revision=self.current()['revision'];barrier=threading.Barrier(2)
        def save(note):
            barrier.wait(timeout=5)
            try:return self.store.save_block(self.pid,self.bid,{'notes':note},revision)['revision']
            except StudioError as e:return e.status
        with ThreadPoolExecutor(max_workers=2) as pool:results=list(pool.map(save,['author','agent']))
        self.assertCountEqual([revision+1,409],results);self.assertIn(self.block()['notes'],['author','agent'])
        self.assertEqual(revision+1,self.current()['revision'])

    def test_reference_cycle_renumbers_prompt_once_and_preserves_guide_identity(self):
        p=self.current();result=self.store.operation(self.pid,p['revision'],'reorder_references',{'block_id':self.bid,'order':['ref-3','ref-1','ref-2']})
        b=self.block(result);self.assertEqual(['ref-3','ref-1','ref-2'],[r['id'] for r in b['references']])
        self.assertEqual('alpha <Picture 2>; beta <Picture 3>; gamma <Picture 1>',split_prompt(b['prompt'])['subject_definitions'])
        self.assertEqual([{'reference_id':'ref-3','frame':0}],b['guides']);self.assertEqual([],self.store.validate(result,b)['errors'])

    def test_invalid_reference_permutations_do_not_commit(self):
        p=self.current()
        for order in [['ref-1','ref-1','ref-3'],['ref-1','ref-2'],['ref-1','ref-2','unknown']]:
            with self.subTest(order=order),self.assertRaises(StudioError):self.store.operation(self.pid,p['revision'],'reorder_references',{'block_id':self.bid,'order':order})
        self.assertEqual(p,self.current())

    def test_seed_bounds_reject_before_commit(self):
        p=self.current();settings=self.block(p)['settings']
        for seed in ['-1',str(2**64),'1.5','not-a-seed']:
            with self.subTest(seed=seed),self.assertRaises(StudioError):self.store.save_block(self.pid,self.bid,{'settings':{**settings,'seed':seed}},p['revision'])
        self.assertEqual(p,self.current())

    def test_timing_rounds_to_profile_grid_and_removes_only_context(self):
        cases=[(2,56),(5,124),(8,192),(56.5/24,56)]
        for seconds,frames in cases:
            with self.subTest(seconds=seconds):
                plain=timing(seconds,'guided');context=timing(seconds,'context')
                self.assertEqual(frames,plain['frames']);self.assertEqual(frames,plain['new_frames'])
                self.assertEqual(frames-22,context['new_frames']);self.assertAlmostEqual((frames-22)/24,context['usable_seconds'])
        for seconds in [1.999,8.001,float('nan'),float('inf'),'abc']:
            with self.subTest(seconds=seconds),self.assertRaises(StudioError):timing(seconds)

    def test_boundary_guides_and_duplicate_last_index_are_validated(self):
        p=self.current();b=copy.deepcopy(self.block(p));b['guides']=[{'reference_id':'ref-1','frame':0},{'reference_id':'ref-3','frame':-1}]
        self.assertEqual([],self.store.validate(p,b)['errors'])
        b['guides'].append({'reference_id':'ref-2','frame':55})
        self.assertIn('Две опоры назначены на один кадр.',self.store.validate(p,b)['errors'])
        for index in [-2,56]:
            b['guides']=[{'reference_id':'ref-1','frame':index}]
            self.assertIn('Индекс опоры выходит за длину источника.',self.store.validate(p,b)['errors'])

    def test_reference_path_escape_and_unsupported_asset_are_refused(self):
        outside=self.base/'private.png';outside.write_bytes(b'outside')
        for path in [outside,'../private.png']:
            with self.subTest(path=str(path)),self.assertRaises(StudioError) as caught:self.store.safe(path)
            self.assertEqual(403,caught.exception.status)
        with self.assertRaises(StudioError):self.store.asset(self.current(),self.plan)

    def test_context_requires_paired_av_and_matching_size(self):
        take,latent=self.add_take();p=self.current();b=copy.deepcopy(self.block(p));b['settings']['mode']='context';b['context_take_id']=take['id']
        good=self.store.validate(p,b);self.assertEqual([],good['errors']);self.assertEqual(34,good['timing']['new_frames'])
        self.assertTrue(any('стартовый Guide' in w for w in good['warnings']))
        b['settings']['size']=[1280,736];self.assertIn('Размер продолжения должен совпадать с исходным AV-latent.',self.store.validate(p,b)['errors'])
        header=json.dumps({'video':{'shape':[1,24,17,32,56]}}).encode();latent.write_bytes(struct.pack('<Q',len(header))+header)
        self.assertIn('Нужен парный native AV-latent.',self.store.validate(p,b)['errors'])

    def test_truncated_av_header_is_a_validation_error_not_an_exception(self):
        take,latent=self.add_take();latent.write_bytes(b'bad')
        p=self.current();b=copy.deepcopy(self.block(p));b['settings']['mode']='context';b['context_take_id']=take['id']
        self.assertIn('Не удалось проверить native AV-latent.',self.store.validate(p,b)['errors'])

    def test_nonobject_av_headers_are_validation_errors(self):
        take,latent=self.add_take();p=self.current();b=copy.deepcopy(self.block(p));b['settings']['mode']='context';b['context_take_id']=take['id']
        for invalid in [[],None,{'video':None,'audio':{}},{'video':{'shape':None},'audio':{}}]:
            encoded=json.dumps(invalid).encode();latent.write_bytes(struct.pack('<Q',len(encoded))+encoded)
            with self.subTest(header=invalid):self.assertIn('Не удалось проверить native AV-latent.',self.store.validate(p,b)['errors'])

    def test_malformed_prompt_missing_picture_or_sound_is_not_ready(self):
        p=self.current();b=copy.deepcopy(self.block(p));b['prompt']=b['prompt'].replace('<Picture 3>','<Picture 4>').replace('overall_soundscape:\nN/A','overall_soundscape:\nrain')
        errors=self.store.validate(p,b)['errors']
        self.assertTrue(any('Метки Picture' in e for e in errors));self.assertTrue(any('оба звуковых' in e for e in errors))

    def test_review_selection_is_distinct_from_acceptance_and_rejection_clears_it(self):
        take,_=self.add_take();p=self.current()
        p=self.store.operation(self.pid,p['revision'],'review_take',{'block_id':self.bid,'take_id':take['id'],'state':'selected'})
        self.assertEqual(take['id'],self.block(p)['selected_take_id']);self.assertEqual('selected',self.block(p)['takes'][0]['review_state'])
        p=self.store.operation(self.pid,p['revision'],'review_take',{'block_id':self.bid,'take_id':take['id'],'state':'accepted','notes':'Проверено движение'})
        self.assertEqual('accepted',self.block(p)['takes'][0]['review_state']);self.assertEqual('Проверено движение',self.block(p)['takes'][0]['notes'])
        p=self.store.operation(self.pid,p['revision'],'review_take',{'block_id':self.bid,'take_id':take['id'],'state':'rejected'})
        self.assertIsNone(self.block(p)['selected_take_id']);self.assertEqual('rejected',self.block(p)['takes'][0]['review_state'])

    def test_continuation_uses_actual_last_frame_and_wraps_seed_without_changing_source(self):
        self.save({'settings':{**self.block()['settings'],'seed':str(2**64-1)}});take,_=self.add_take();p=self.current();original=copy.deepcopy(self.block(p))
        p=self.store.operation(self.pid,p['revision'],'create_continuation',{'block_id':self.bid,'take_id':take['id']})
        new=p['blocks'][-1];self.assertNotEqual(self.bid,new['id']);self.assertEqual(original,self.block(p))
        self.assertEqual('context',new['settings']['mode']);self.assertEqual('0',new['settings']['seed']);self.assertEqual(take['id'],new['context_take_id'])
        self.assertEqual([{'reference_id':'ref-3','frame':0}],new['guides'])
        self.assertEqual(take['last_frame_asset_id'],next(r for r in new['references'] if r['id']=='ref-3')['asset_id'])
        self.assertEqual([],new['takes']);self.assertIsNone(new['selected_take_id']);self.assertEqual('draft',new['status'])

    def test_restore_fields_retains_newer_takes_and_creates_new_revision(self):
        before=self.current();self.save({'title':'Изменено'})
        history=next(h for h in self.store.history(self.pid) if h['revision']==before['revision'])
        take,_=self.add_take();current=self.current()
        restored=self.store.operation(self.pid,current['revision'],'restore_block',{'block_id':self.bid,'history_file':history['file']})
        self.assertEqual('Исходный блок',self.block(restored)['title']);self.assertEqual([take['id']],[t['id'] for t in self.block(restored)['takes']])
        self.assertEqual(current['revision']+1,restored['revision'])

    def test_agent_context_contains_real_paths_and_revision_without_jobs(self):
        c=self.store.context(self.pid,self.bid)
        self.assertEqual(self.current()['revision'],c['project']['revision']);self.assertEqual([1,2,3],[a['picture'] for a in c['assets']])
        self.assertEqual([str(p.resolve()) for p in self.images],[a['absolute_path'] for a in c['assets']])
        self.assertEqual(self.initial_prompt,c['block']['prompt']);self.assertFalse((self.store.location(self.pid).parent/'Jobs').exists())

    def test_import_comparison_is_asset_and_new_is_the_only_generation_take(self):
        folder=self.amv/'Clair';shots=folder/'Shots';videos=folder/'video';shots.mkdir(parents=True);videos.mkdir()
        card=shots/'CL-AR01.md';card.write_text('# CL-AR01 — Контрольный блок\n',encoding='utf-8')
        raw=videos/'CL-AR01.v1.Raw.mp4';new=videos/'CL-AR01.v1.New.mp4';comparison=videos/'CL-AR01.v1.Compare.mp4'
        for file in [raw,new,comparison]:file.write_bytes(file.name.encode())
        original={file:digest(file) for file in [card,raw,new,comparison]}
        imported=self.store.create('Импорт',folder,'archive',True);block=imported['blocks'][0]
        self.assertEqual(['CL-AR01.v1'],[take['name'] for take in block['takes']]);self.assertIsNone(block['selected_take_id'])
        take=block['takes'][0];self.assertEqual('unreviewed',take['review_state']);self.assertEqual(new.resolve(),self.store.resolve_asset(imported,take['video_asset_id']))
        self.assertIn(comparison.resolve(),[self.store.resolve_asset(imported,aid) for aid in imported['assets']])
        self.assertEqual(original,{file:digest(file) for file in original})

    def test_reconcile_only_removes_unreviewed_imported_comparison_takes(self):
        take,_=self.add_take();p=self.current();block=self.block(p);video=self.store.resolve_asset(p,take['video_asset_id']);before_digest=digest(video)
        rows=[]
        for ident,name,state,imported in [('remove','CL-T01.Compare','unreviewed',True),('accepted','CL-T01.Compare.accepted','accepted',True),('selected','CL-T01.Compare.selected','unreviewed',True),('generated','CL-T01.Compare.generated','unreviewed',False),('normal','CL-T01.v1','unreviewed',True)]:
            rows.append({**take,'id':ident,'name':name,'review_state':state,'imported':imported})
        block['takes']=rows;block['selected_take_id']='selected';p=self.store.commit(p,p['revision'],'fixture','Import history');assets=copy.deepcopy(p['assets'])
        self.store.reconcile_imports();result=self.current()
        self.assertEqual(['accepted','selected','generated','normal'],[t['id'] for t in self.block(result)['takes']])
        self.assertEqual('selected',self.block(result)['selected_take_id']);self.assertEqual(p['revision']+1,result['revision'])
        self.assertEqual(assets,result['assets']);self.assertEqual(before_digest,digest(video))
        self.store.reconcile_imports();self.assertEqual(result,self.current())

    def test_seed_rebuilds_registry_without_overwriting_existing_project(self):
        original=self.store.create('Авторское имя',self.amv/'Clair','claire');state=self.store.location('claire');before_digest=digest(state)
        rebuilt=Store(self.book,self.amv,self.base/'recovered-registry');rebuilt.seed()
        self.assertEqual(['claire'],[p['id'] for p in rebuilt.list_projects()]);self.assertEqual(original,rebuilt.get('claire'))
        self.assertEqual(before_digest,digest(state));rebuilt.seed();self.assertEqual(original,rebuilt.get('claire'))
