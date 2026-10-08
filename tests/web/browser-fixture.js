'use strict';
const source = id => ({sourceId:id,sourceOrdinal:7,span:{start:0,end:8},availability:'source-catalog',
  sourceText:'<img src=x onerror=alert(1)> body',selectedText:'<img src',coordinates:{page:1}});
const node = (id, parentStatus='no-validated-parent', parentId=null) => ({elementId:id,stableId:'stable-'+id,
  sourceId:'source-'+id,text:'Duplicate <img src=x onerror=alert(1)>',level:null,parentId,parentStatus,
  decision:{origin:'model',status:'RequiresReview',confidenceBasis:'source-bound',disputed:false},
  validation:{sourceSelectionValid:true},sources:[source('source-'+id)],inlineBody:'body'});
const fixture = {schemaVersion:'web-pipeline-v2',executionId:'synthetic-browser-fixture',availability:'same-execution',
  outcome:'NeedsHumanReview',sourceKind:'pdf',headings:[node('root'),node('child','validated-parent','root'),
  node('grandchild','validated-parent','child'),node('second'),node('orphan','parent-not-emitted','hidden')],
  relations:[{fromId:'root',toId:'child',type:'ParentChild'},{fromId:'child',toId:'grandchild',type:'ParentChild'}],
  summary:{accepted:5,unknownLevel:5,requiresReview:5,rejectedAvailability:'not-recorded'},
  stages:[{id:'source-parsing',status:'completed',evidence:'synthetic fixture'},
    {id:'F1-semantic-function',status:'not-recorded',evidence:'No per-stage observation'},
    {id:'final-result',status:'completed',evidence:'synthetic fixture'}],checkpoints:[],audit:null,provenance:null};
fixture.headings[1].sources.push(source('multipart-second'));
document.getElementById('load').onclick = () => WebPipelineV2.render(fixture);
document.getElementById('empty').onclick = () => WebPipelineV2.render({...fixture,headings:[],relations:[],
  summary:{accepted:0,unknownLevel:0,requiresReview:0,rejectedAvailability:'not-recorded'}});
document.getElementById('failure').onclick = () => WebPipelineV2.showFailure('<script>error fixture</script>');
document.getElementById('unavailable').onclick = () => WebPipelineV2.render({availability:'unavailable'});
